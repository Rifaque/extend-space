using System.Runtime.CompilerServices;

namespace ExtendSpace.Core.Sessions;

public enum SessionStartResult
{
    /// <summary>A session was begun.</summary>
    Started,

    /// <summary>A session is already in progress, being torn down, or failed and not yet acknowledged.</summary>
    AlreadyActive,

    /// <summary>
    /// A previous session's teardown overran its deadline and something it started has not
    /// returned yet. No new session may run beside it.
    /// </summary>
    PreviousSessionNotReleased,
}

/// <summary>What the Connect screen needs from the session's owner: its state, and three intents.</summary>
public interface ISessionControl
{
    SessionState State { get; }

    /// <summary>Raised after every state change, on whichever thread made it.</summary>
    event EventHandler? StateChanged;

    /// <summary>Begins a session. Returns at once; the work happens on the coordinator's inputs.</summary>
    SessionStartResult Start(SessionRequest request);

    /// <summary>Ends the session in progress. Returns at once. False if there is nothing to stop.</summary>
    bool Stop();

    /// <summary>Clears a failure the user has seen. False if there is no failure.</summary>
    bool AcknowledgeError();
}

public sealed record SessionCoordinatorOptions
{
    /// <summary>How long connection attempts may go on failing, from the first attempt or from a loss, before the session fails.</summary>
    public TimeSpan RetryBudget { get; init; } = RetryBackoff.DefaultBudget;

    /// <summary>
    /// The whole of teardown, provisionally (decision D26, device test V7). A step still running when
    /// it passes is abandoned and recorded; the session reaches a terminal phase regardless.
    /// </summary>
    public TimeSpan TeardownDeadline { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Chooses the stream once the client's capabilities are known. Null means none fits.</summary>
    public Func<VideoSourceDescription, ClientCapabilities, EncodingRequest?> ChooseEncoding { get; init; } = StreamConfigurationPolicy.Choose;

    /// <summary>The choice for an Extend session (D89): the stream takes the virtual monitor's shape.</summary>
    public Func<VideoSourceDescription, ClientCapabilities, EncodingRequest?> ChooseExtendEncoding { get; init; } = StreamConfigurationPolicy.ChooseShaped;
}

/// <summary>
/// The one owner of the host's session (architecture section 4). It decides; the connection and
/// the pipeline do. Once a session exists it is the only thing that dispatches into the state
/// machine, the only thing that retries, and the only thing that asks for the keyframe that starts
/// a stream.
///
/// <para><b>Concurrency.</b> Every input (an intent from the UI, the completion of a call on a seam,
/// a clock delay ending) is processed one at a time through a run-to-completion queue, on the
/// thread that delivered it or on the thread already draining the queue. The coordinator has no
/// thread of its own and makes no thread hops: when every seam completes on one thread, as test
/// fakes do, the whole coordinator runs on that thread and a test needs neither sleeps nor timeouts.</para>
///
/// <para><b>Startup</b> follows section 5.1: acquire the source, open the connection, exchange
/// capabilities, choose and prepare the encoder (first connection only), configure the stream,
/// start media and ask for a keyframe. A retryable failure of an attempt is reported as
/// <see cref="SessionEvent.AttemptFailed"/> and retried from "open" after a backoff, within the
/// budget. Pipeline failures are terminal whatever treatment they carry: no retry path for the
/// pipeline has been designed or tested.</para>
///
/// <para><b>Teardown</b> follows section 5.2: cancel the run; wait for the call in progress; stop the
/// pipeline; close the connection; then <see cref="SessionEvent.TeardownCompleted"/> or
/// <see cref="SessionEvent.Failed"/>, last. All of it is bounded by one deadline on the clock.</para>
/// </summary>
public sealed class SessionCoordinator : ISessionControl
{
    private const int MaxTeardownFaults = 16;
    private const string StoppedOnThePc = "the session was stopped on the PC";
    private const string EndedByTheDevice = "the device ended the session";

    /// <summary>The last client's hello and capabilities, kept after the session for the display-mode advice (D92).</summary>
    public ClientCapabilities? LastClient { get; private set; }

    private readonly SessionStateMachine _machine;
    private readonly ISessionConnection _connection;
    private readonly IVideoPipeline _pipeline;
    private readonly ISessionClock _clock;
    private readonly SessionCoordinatorOptions _options;
    private readonly SerialInbox _inbox = new();
    private readonly Lock _intent = new();
    private readonly EncodedVideoSink _sink;

    // Touched only on the inbox.
    private Run? _run;

    // Read from other threads.
    private volatile bool _forwardKeyframes;
    private int _unreleased;
    private IReadOnlyList<SessionFault> _lastTeardownFaults = [];

    public SessionCoordinator(
        SessionStateMachine machine,
        ISessionConnection connection,
        IVideoPipeline pipeline,
        ISessionClock clock,
        SessionCoordinatorOptions? options = null)
    {
        _machine = machine;
        _connection = connection;
        _pipeline = pipeline;
        _clock = clock;
        _options = options ?? new SessionCoordinatorOptions();
        _sink = (accessUnit, captureMicros) => _connection.OfferVideo(accessUnit, captureMicros);
        _connection.KeyframeWanted += OnKeyframeWanted;
        _machine.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
    }

    public event EventHandler? StateChanged;

    public SessionState State => _machine.State;

    /// <summary>
    /// False while a step that a teardown abandoned has not yet returned (or, if it acquired
    /// something on its way, not yet been undone). <see cref="Start"/> is refused meanwhile.
    /// </summary>
    public bool IsReleased => Volatile.Read(ref _unreleased) == 0;

    /// <summary>Inputs the coordinator failed to process because its own code threw. Zero unless it has a bug.</summary>
    public int UnhandledInputFailures => _inbox.Failures;

    /// <summary>Cleanup faults of the most recent teardown: steps that threw or overran. At most 16.</summary>
    public IReadOnlyList<SessionFault> LastTeardownFaults => Volatile.Read(ref _lastTeardownFaults);

    public SessionStartResult Start(SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_intent)
        {
            if (!IsReleased)
            {
                return SessionStartResult.PreviousSessionNotReleased;
            }

            // The state machine's compare-and-set is the one duplicate-start guard.
            if (!_machine.Dispatch(new SessionEvent.ConnectRequested(request)))
            {
                return SessionStartResult.AlreadyActive;
            }

            _inbox.Enqueue(() => BeginRun(request));
        }

        _inbox.Drain();
        return SessionStartResult.Started;
    }

    public bool Stop()
    {
        lock (_intent)
        {
            if (!_machine.Dispatch(new SessionEvent.DisconnectRequested()))
            {
                return false;
            }

            _inbox.Enqueue(OnStopRequested);
        }

        _inbox.Drain();
        return true;
    }

    public bool AcknowledgeError()
    {
        lock (_intent)
        {
            return _machine.Dispatch(new SessionEvent.ErrorAcknowledged());
        }
    }

    private void OnKeyframeWanted()
    {
        // Any thread. The pipeline takes a request at any time; before it is acquired or after it
        // is stopped there is nothing to ask, and the contract says it would ignore the request.
        if (_forwardKeyframes)
        {
            _pipeline.RequestKeyframe();
        }
    }

    private void BeginRun(SessionRequest request)
    {
        var run = new Run(request, new RetryBackoff(_options.RetryBudget));
        _run = run;
        _ = DriveAsync(run);
    }

    private void OnStopRequested()
    {
        // The state machine already says Disconnecting. If a teardown is already under way (a
        // failure's, or one begun because this stop's dispatch was seen first), it finishes as a
        // stop; otherwise this begins one.
        if (_run is { Ending: false } run)
        {
            BeginTeardown(run, Ending.Stop(StoppedOnThePc));
        }
    }

    // ---- Startup and streaming -------------------------------------------------------------

    private async Task DriveAsync(Run run)
    {
        Task driving = DriveCoreAsync(run);
        await Settle(driving);

        // Nothing in the coordinator is expected to throw; if it does, the session ends with the
        // reason rather than being left in a phase nothing will move it out of.
        if (driving.IsFaulted && !run.Ending)
        {
            Exception exception = driving.Exception!.InnerException ?? driving.Exception;
            BeginTeardown(run, Ending.Fail(SessionFault.Unexpected(exception), run.HasStreamed));
        }
    }

    private async Task DriveCoreAsync(Run run)
    {
        // Step 1: acquire the source. No encoder exists yet.
        Task<AttemptOutcome<VideoSourceDescription>> acquired = await Call(run, Component.Pipeline, "acquire the display",
            token => _pipeline.AcquireAsync(new VideoSourceRequest(run.Request.MonitorIndex, run.Request.MonitorDeviceName), token));
        if (run.Ending || Outcome(run, acquired, pipelineStep: true) is not { } source)
        {
            return;
        }

        run.Source = source;
        run.PipelineAcquired = true;
        _forwardKeyframes = true;
        _ = WatchPipelineAsync(run);

        run.BudgetStart = _clock.Elapsed;
        SessionFault? lostBecause = null;
        while (true)
        {
            // Steps 2 to 4, retried as a whole from "open" until an attempt reaches Streaming.
            if (!await Inbox(AttemptUntilStreamingAsync(run, lostBecause)))
            {
                return;
            }

            // Steps 5 and 6.
            if (!EnterStreaming(run))
            {
                return;
            }

            Task<ConnectionEnd> ended = await Call(run, Component.Connection, "wait for the connection to end", _connection.WaitForEndAsync);
            if (run.Ending)
            {
                return;
            }

            ConnectionEnd end = ended.IsCompletedSuccessfully
                ? ended.Result
                : new ConnectionEnd(ConnectionEndKind.Fatal, ended.IsFaulted ? SessionFault.Unexpected(ended.Exception!.InnerException ?? ended.Exception) : Unrequested());

            switch (end.Kind)
            {
                case ConnectionEndKind.Lost when end.Fault is not { IsRetryable: false }:
                    SessionFault cause = end.Fault ?? new SessionFault(FaultCategory.Connection, SessionFaultCodes.ConnectionLostWithoutReason, "The connection to the device was lost.", FaultTreatment.Retryable);

                    // Nothing stale is kept for the next connection: frames are released unencoded
                    // until it is streaming again. Capture and the encoder stay open.
                    _pipeline.SetEncoding(false);
                    if (!Dispatch(run, new SessionEvent.ConnectionLost(cause)))
                    {
                        return;
                    }

                    // A new budget, measured from the loss.
                    run.Backoff.Reset();
                    run.BudgetStart = _clock.Elapsed;
                    lostBecause = cause;
                    continue;

                case ConnectionEndKind.PeerEnded:
                    // Not an error: the device ended the session, which is not to be re-established.
                    // If a stop got there first, Dispatch has already begun its teardown.
                    if (Dispatch(run, new SessionEvent.DisconnectRequested()))
                    {
                        BeginTeardown(run, Ending.Stop(EndedByTheDevice));
                    }

                    return;

                default:
                    // Fatal; a loss that says it cannot be recovered; or Closed, which this side did
                    // not ask for while the run was going on.
                    BeginTeardown(run, Ending.Fail(AsTerminal(end.Fault ?? Unrequested()), run.HasStreamed));
                    return;
            }
        }
    }

    /// <summary>
    /// Makes attempts until one reaches Streaming (true), or the run is ending (false). Every failed
    /// attempt is reported, then waited out, within the budget. After a loss
    /// (<paramref name="lostBecause"/> set) the first attempt is waited for too, as the experiment
    /// did: a connection that has just dropped is not usually back within a millisecond.
    /// </summary>
    private async Task<bool> AttemptUntilStreamingAsync(Run run, SessionFault? lostBecause)
    {
        SessionFault? last = lostBecause;
        while (true)
        {
            if (last is not null)
            {
                if (run.Backoff.NextDelay(_clock.Elapsed - run.BudgetStart) is not { } delay)
                {
                    string detail = $"No connection could be {(run.HasStreamed ? "re-established" : "established")} within {_options.RetryBudget.TotalSeconds:0.#} s.";
                    BeginTeardown(run, Ending.Fail(last, run.HasStreamed, detail, tellClient: false));
                    return false;
                }

                await Call(run, Component.Clock, "wait before the next attempt", token => AsStep(_clock.DelayAsync(delay, token)));
                if (run.Ending)
                {
                    return false;
                }
            }

            SessionFault? failure = await Inbox(AttemptAsync(run));
            if (run.Ending)
            {
                return false;
            }

            if (failure is null)
            {
                return true;
            }

            if (!failure.IsRetryable)
            {
                BeginTeardown(run, Ending.Fail(failure, run.HasStreamed));
                return false;
            }

            if (!Dispatch(run, new SessionEvent.AttemptFailed(failure)))
            {
                return false;
            }

            last = failure;
        }
    }

    /// <summary>
    /// One attempt from "open" to "stream configured". Null when it succeeded or the run is ending
    /// (the caller tells which); otherwise the fault.
    /// </summary>
    private async Task<SessionFault?> AttemptAsync(Run run)
    {
        // Step 2.
        run.ConnectionTouched = true;
        Task<AttemptOutcome<Done>> opened = await Call(run, Component.Connection, "open the connection",
            token => AsStep(_connection.OpenAsync(token)));
        if (run.Ending || EndedByPeer(run, opened))
        {
            return null;
        }

        if (OutcomeOrFault(run, opened, out _) is { } openFailure)
        {
            return openFailure;
        }

        if (!Dispatch(run, new SessionEvent.TransportConnected()))
        {
            return null;
        }

        // Step 3.
        Task<AttemptOutcome<ClientCapabilities>> exchanged = await Call(run, Component.Connection, "exchange capabilities", _connection.ExchangeCapabilitiesAsync);
        if (run.Ending || EndedByPeer(run, exchanged))
        {
            return null;
        }

        if (OutcomeOrFault(run, exchanged, out ClientCapabilities? client) is { } exchangeFailure)
        {
            return exchangeFailure;
        }

        LastClient = client;

        // Step 4: on the first connection, choose and prepare; on a later one the stream exists
        // and the returning client must still be able to take it. Re-preparing is not supported.
        if (run.Settings is null)
        {
            Func<VideoSourceDescription, ClientCapabilities, EncodingRequest?> choose = run.Request.Mode == DisplayMode.Extend ? _options.ChooseExtendEncoding : _options.ChooseEncoding;
            if (choose(run.Source!, client!) is not { } encoding)
            {
                return NoCommonConfiguration("No decoder the device lists can take any stream configuration this host offers.");
            }

            Task<AttemptOutcome<StreamSettings>> prepared = await Call(run, Component.Pipeline, "prepare the encoder",
                token => _pipeline.PrepareAsync(encoding, token));
            if (run.Ending || Outcome(run, prepared, pipelineStep: true) is not { } settings)
            {
                return null;
            }

            run.Settings = settings;
        }
        else if (!client!.CanDecode(run.Settings.Width, run.Settings.Height))
        {
            return NoCommonConfiguration($"The device that returned cannot decode the {run.Settings.Width} x {run.Settings.Height} stream already running.");
        }

        Task<AttemptOutcome<PeerDescription>> configured = await Call(run, Component.Connection, "configure the stream",
            token => _connection.ConfigureStreamAsync(run.Settings, token));
        if (run.Ending || EndedByPeer(run, configured))
        {
            return null;
        }

        return OutcomeOrFault(run, configured, out _);
    }

    /// <summary>
    /// True when the client said <c>bye</c> during a connection step (decision D44). The session
    /// then ends exactly as for a <c>bye</c> while streaming: <see cref="SessionEvent.DisconnectRequested"/>,
    /// teardown, <see cref="SessionEvent.TeardownCompleted"/>. No fault, no error, no retry.
    /// </summary>
    private bool EndedByPeer<T>(Run run, Task<AttemptOutcome<T>> step)
        where T : class
    {
        if (!step.IsCompletedSuccessfully || step.Result.Kind != OutcomeKind.PeerEnded)
        {
            return false;
        }

        // If a stop was dispatched first, Dispatch has already begun its teardown.
        if (Dispatch(run, new SessionEvent.DisconnectRequested()))
        {
            BeginTeardown(run, Ending.Stop(EndedByTheDevice));
        }

        return true;
    }

    /// <summary>Steps 5 and 6. False if a stop overtook it.</summary>
    private bool EnterStreaming(Run run)
    {
        if (!Dispatch(run, new SessionEvent.NegotiationCompleted()))
        {
            return false;
        }

        run.HasStreamed = true;
        if (!run.PipelineStarted)
        {
            _pipeline.Start(_sink);
            run.PipelineStarted = true;
        }

        _pipeline.SetEncoding(true);

        // The keyframe that starts every connection's stream is asked for here and only here
        // (decision D27). The pipeline owes it until it has produced one, even on a still screen.
        _pipeline.RequestKeyframe();
        return true;
    }

    /// <summary>
    /// Dispatches a "what happened" event. The machine rejects one only when a stop was dispatched
    /// from another thread before the stop's own input reached the inbox; the run is then ended as
    /// a stop here, and that input finds nothing left to do.
    /// </summary>
    private bool Dispatch(Run run, SessionEvent sessionEvent)
    {
        if (_machine.Dispatch(sessionEvent))
        {
            return true;
        }

        BeginTeardown(run, Ending.Stop(StoppedOnThePc));
        return false;
    }

    private async Task WatchPipelineAsync(Run run)
    {
        Task<SessionFault?> watched = Invoke(() => _pipeline.WaitForFaultAsync(run.Watching.Token));
        await Settle(watched);
        if (run.Ending || watched.IsCanceled)
        {
            return;
        }

        SessionFault fault = watched.IsFaulted
            ? SessionFault.Unexpected(watched.Exception!.InnerException ?? watched.Exception)
            : watched.Result ?? new SessionFault(FaultCategory.Internal, SessionFaultCodes.PipelineStoppedUnexpectedly, "The video pipeline stopped while the session needed it.", FaultTreatment.Terminal);
        BeginTeardown(run, Ending.Fail(AsTerminal(fault), run.HasStreamed));
    }

    // ---- Outcomes ------------------------------------------------------------------------

    /// <summary>The value of a successful pipeline step; otherwise ends the run (any failure of a pipeline step is terminal) and returns null.</summary>
    private T? Outcome<T>(Run run, Task<AttemptOutcome<T>> step, bool pipelineStep)
        where T : class
    {
        if (OutcomeOrFault(run, step, out T? value) is { } fault)
        {
            BeginTeardown(run, Ending.Fail(pipelineStep ? AsTerminal(fault) : fault, run.HasStreamed));
            return null;
        }

        return run.Ending ? null : value;
    }

    /// <summary>Null and the value when the step succeeded; the fault when it failed. A cancellation nobody asked for is a fault.</summary>
    private static SessionFault? OutcomeOrFault<T>(Run run, Task<AttemptOutcome<T>> step, out T? value)
        where T : class
    {
        value = null;
        if (step.IsFaulted)
        {
            return SessionFault.Unexpected(step.Exception!.InnerException ?? step.Exception);
        }

        AttemptOutcome<T>? outcome = step.IsCanceled ? null : step.Result;
        if (outcome is { Kind: OutcomeKind.Succeeded })
        {
            value = outcome.Value;
            return null;
        }

        if (outcome is { Kind: OutcomeKind.Failed })
        {
            return outcome.Fault;
        }

        if (outcome is { Kind: OutcomeKind.PeerEnded })
        {
            // Only the connection's steps can learn that the client ended the session, and they
            // are checked for it before this. Anywhere else it is a broken contract.
            return new SessionFault(FaultCategory.Internal, SessionFaultCodes.UnexpectedPeerEnded, "A step that cannot hear from the client reported that the client ended the session.", FaultTreatment.Terminal);
        }

        // Cancelled. If the run is ending this is the cancellation it asked for and changes
        // nothing; otherwise the component broke its contract, which nothing can retry.
        return run.Ending ? null : Unrequested();
    }

    private static SessionFault Unrequested() => new(
        FaultCategory.Internal,
        SessionFaultCodes.UnrequestedCancellation,
        "A step reported that it was cancelled although nothing had cancelled it.",
        FaultTreatment.Terminal);

    private static SessionFault NoCommonConfiguration(string message) =>
        new(FaultCategory.Protocol, SessionFaultCodes.NoCommonConfiguration, message, FaultTreatment.Terminal);

    private static SessionFault AsTerminal(SessionFault fault) =>
        fault.IsRetryable ? fault with { Treatment = FaultTreatment.Terminal } : fault;

    // These convert without an await, so that the conversion runs on the thread that completed
    // the call, like the call's own completion.
    private static Task<AttemptOutcome<Done>> AsStep(Task<AttemptOutcome> call) => call.ContinueWith(
        static completed =>
        {
            if (completed.IsCanceled)
            {
                return AttemptOutcome<Done>.Cancelled;
            }

            AttemptOutcome outcome = completed.GetAwaiter().GetResult();
            return outcome.Kind switch
            {
                OutcomeKind.Succeeded => AttemptOutcome<Done>.Succeeded(Done.Value),
                OutcomeKind.Failed => AttemptOutcome<Done>.Failed(outcome.Fault!),
                OutcomeKind.PeerEnded => AttemptOutcome<Done>.PeerEnded(outcome.PeerReason!),
                _ => AttemptOutcome<Done>.Cancelled,
            };
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    /// <summary>A delay as a step: elapsed or cancelled, it is over; only a fault is passed on.</summary>
    private static Task<Done> AsStep(Task delay) => delay.ContinueWith(
        static completed =>
        {
            if (completed.IsFaulted)
            {
                completed.GetAwaiter().GetResult();
            }

            return Done.Value;
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    // ---- Teardown --------------------------------------------------------------------------

    private void BeginTeardown(Run run, Ending ending)
    {
        if (run.Ending)
        {
            return;
        }

        run.Ending = true;
        _forwardKeyframes = false;
        _ = TeardownAsync(run, ending);
    }

    private async Task TeardownAsync(Run run, Ending ending)
    {
        // Step 0: everything in progress is asked to stop, including any backoff delay.
        run.Cancellation.Cancel();
        run.Watching.Cancel();
        var teardown = new Teardown(_clock.Elapsed + _options.TeardownDeadline);

        try
        {
            // The call in progress is waited for, within the deadline, so that nothing of this run
            // is started on a component while another of its calls is still running there.
            Task? inFlight = run.InFlight;
            Component inFlightOwner = run.InFlightOwner;
            if (inFlight is { IsCompleted: false })
            {
                await Settle(Bounded(run, teardown, $"{run.InFlightName} (in progress when the session began to end)", inFlight, inFlightOwner));
            }

            // A pipeline call still running when the run began ending may have acquired something
            // since (it succeeded inside the deadline), or may yet (it was abandoned).
            bool pipelineCallOutstanding = inFlightOwner == Component.Pipeline && inFlight is not null
                && (!inFlight.IsCompleted || Succeeded(inFlight));

            // Step 1: the pipeline, if anything of it may be held.
            if (run.PipelineAcquired || pipelineCallOutstanding)
            {
                await Settle(Bounded(run, teardown, "stop the video pipeline", Invoke(() => _pipeline.StopAsync(teardown.Token)), Component.None));
            }

            // Step 2: the connection and the transport, if an attempt to open was ever made.
            if (run.ConnectionTouched)
            {
                SessionFault? toClient = ending.TellClient ? ending.Fault : null;
                await Settle(Bounded(run, teardown, "close the connection", Invoke(() => _connection.CloseAsync(ending.Reason, toClient, teardown.Token)), Component.None));
            }
        }
        finally
        {
            teardown.Dispose();
            Volatile.Write(ref _lastTeardownFaults, run.CleanupFaults.ToArray());
            _run = null;

            // Last, so that whoever sees the terminal phase can rely on nothing of the run running
            // except what was abandoned, which IsReleased reports.
            bool failed = ending.Error is { } error && _machine.Dispatch(new SessionEvent.Failed(error));
            if (!failed)
            {
                // A stop, or a failure whose teardown a stop overtook (the state is Disconnecting
                // and cannot fail any more).
                _machine.Dispatch(new SessionEvent.TeardownCompleted());
            }
        }
    }

    /// <summary>
    /// Awaits <paramref name="step"/> until the teardown deadline. A step still running then is
    /// abandoned: recorded as a cleanup fault and tracked until it returns, and if it was a call
    /// that acquires something and it later succeeds, what it acquired is released.
    /// </summary>
    private async Task Bounded(Run run, Teardown teardown, string name, Task step, Component lateSuccessOwner)
    {
        if (!step.IsCompleted)
        {
            TimeSpan remaining = teardown.Deadline - _clock.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                using var timer = new CancellationTokenSource();
                Task expiry = _clock.DelayAsync(remaining, timer.Token);
                await Settle(Task.WhenAny(step, expiry));
                timer.Cancel();
            }
        }

        if (!step.IsCompleted)
        {
            Record(run, new SessionFault(FaultCategory.Cleanup, SessionFaultCodes.TeardownStepOverran, $"'{name}' had not returned by the teardown deadline and was left running.", FaultTreatment.Terminal));
            teardown.Abandon();
            TrackUntilReleased(step, lateSuccessOwner);
            return;
        }

        if (step.IsFaulted)
        {
            Exception exception = step.Exception!.InnerException ?? step.Exception;
            Record(run, new SessionFault(FaultCategory.Cleanup, SessionFaultCodes.TeardownStepFailed, $"'{name}' failed: {exception.GetType().Name}: {exception.Message}", FaultTreatment.Terminal));
        }
    }

    private void TrackUntilReleased(Task step, Component lateSuccessOwner)
    {
        Interlocked.Increment(ref _unreleased);
        step.ContinueWith(
            completed =>
            {
                // A step abandoned while it was acquiring may still succeed. What it acquired then
                // belongs to no run, and is released here before the session counts as let go.
                Task? undo = lateSuccessOwner switch
                {
                    Component.Pipeline when Succeeded(completed) => Invoke(() => _pipeline.StopAsync(CancellationToken.None)),
                    Component.Connection when Succeeded(completed) => Invoke(() => _connection.CloseAsync("the session had already ended", null, CancellationToken.None)),
                    _ => null,
                };

                if (undo is null)
                {
                    Interlocked.Decrement(ref _unreleased);
                }
                else
                {
                    undo.ContinueWith(_ => Interlocked.Decrement(ref _unreleased), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Whether a step's task finished with a successful outcome, whatever its value type.</summary>
    private static bool Succeeded(Task step) => step.IsCompletedSuccessfully && step switch
    {
        Task<AttemptOutcome<VideoSourceDescription>> acquire => acquire.Result.Kind == OutcomeKind.Succeeded,
        Task<AttemptOutcome<StreamSettings>> prepare => prepare.Result.Kind == OutcomeKind.Succeeded,
        Task<AttemptOutcome<Done>> open => open.Result.Kind == OutcomeKind.Succeeded,
        Task<AttemptOutcome<ClientCapabilities>> exchange => exchange.Result.Kind == OutcomeKind.Succeeded,
        Task<AttemptOutcome<PeerDescription>> configure => configure.Result.Kind == OutcomeKind.Succeeded,
        _ => false,
    };

    private static void Record(Run run, SessionFault fault)
    {
        if (run.CleanupFaults.Count < MaxTeardownFaults)
        {
            run.CleanupFaults.Add(fault);
        }
    }

    // ---- Awaiting on the inbox -------------------------------------------------------------

    /// <summary>
    /// Starts one call on a seam for <paramref name="run"/> and resumes on the inbox when it has
    /// finished, however it finished. Gives back the finished task; never throws for the call.
    /// </summary>
    private InboxAwaitable<Task<T>> Call<T>(Run run, Component owner, string name, Func<CancellationToken, Task<T>> start)
    {
        Task<T> task = Invoke(() => start(run.Cancellation.Token));
        run.InFlight = task;
        run.InFlightName = name;
        run.InFlightOwner = owner;
        return new InboxAwaitable<Task<T>>(task, _inbox, () =>
        {
            if (ReferenceEquals(run.InFlight, task))
            {
                run.InFlight = null;
            }

            return task;
        });
    }

    /// <summary>Awaits a task without observing how it ended.</summary>
    private InboxAwaitable<Task> Settle(Task task) => new(task, _inbox, () => task);

    /// <summary>Awaits one of the coordinator's own steps and gives its result, rethrowing if it threw.</summary>
    private InboxAwaitable<T> Inbox<T>(Task<T> task) => new(task, _inbox, () => task.GetAwaiter().GetResult());

    private static Task<T> Invoke<T>(Func<Task<T>> start)
    {
        try
        {
            return start() ?? Task.FromException<T>(new InvalidOperationException("A seam returned no task."));
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    private static Task Invoke(Func<Task> start)
    {
        try
        {
            return start() ?? Task.FromException(new InvalidOperationException("A seam returned no task."));
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    /// <summary>
    /// The only way coordinator code awaits anything. It resumes through the inbox, after the input
    /// being processed, and never on a captured synchronization context: an ordinary await would
    /// resume on the UI thread whenever the UI thread happened to be draining the inbox.
    /// </summary>
    private readonly struct InboxAwaitable<T>(Task task, SerialInbox inbox, Func<T> result) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => false;

        public InboxAwaitable<T> GetAwaiter() => this;

        public T GetResult() => result();

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            task.ContinueWith(
                static (_, state) =>
                {
                    var (queue, next) = ((SerialInbox, Action))state!;
                    queue.Post(next);
                },
                (inbox, continuation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    // ---- Run state ---------------------------------------------------------------------------

    private enum Component
    {
        None,
        Pipeline,
        Connection,
        Clock,
    }

    private sealed class Done
    {
        public static readonly Done Value = new();
    }

    /// <summary>How a run is to end. A stop has no error; a failure has one.</summary>
    private sealed record Ending(string Reason, SessionFault? Fault, SessionError? Error, bool TellClient)
    {
        public static Ending Stop(string reason) => new(reason, null, null, false);

        public static Ending Fail(SessionFault fault, bool hasStreamed, string? detail = null, bool tellClient = true) =>
            new(fault.Message, fault, SessionErrors.From(fault, hasStreamed, detail), tellClient);
    }

    private sealed class Teardown(TimeSpan deadline) : IDisposable
    {
        private readonly CancellationTokenSource _hurry = new();

        public TimeSpan Deadline { get; } = deadline;

        /// <summary>Given to every teardown step; cancelled once one overruns, to ask the rest to be quick.</summary>
        public CancellationToken Token => _hurry.Token;

        public void Abandon() => _hurry.Cancel();

        public void Dispose() => _hurry.Dispose();
    }

    /// <summary>Everything one accepted start owns. Touched only on the inbox.</summary>
    private sealed class Run(SessionRequest request, RetryBackoff backoff)
    {
        public SessionRequest Request { get; } = request;

        public RetryBackoff Backoff { get; } = backoff;

        /// <summary>Cancelled when the run begins to end: every step and every backoff delay.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Cancels the wait on the pipeline's fault, which is not a step and is not awaited by teardown.</summary>
        public CancellationTokenSource Watching { get; } = new();

        public List<SessionFault> CleanupFaults { get; } = [];

        public bool Ending { get; set; }

        public bool HasStreamed { get; set; }

        public TimeSpan BudgetStart { get; set; }

        public bool PipelineAcquired { get; set; }

        public bool PipelineStarted { get; set; }

        public bool ConnectionTouched { get; set; }

        public VideoSourceDescription? Source { get; set; }

        public StreamSettings? Settings { get; set; }

        public Task? InFlight { get; set; }

        public string InFlightName { get; set; } = string.Empty;

        public Component InFlightOwner { get; set; }
    }
}

/// <summary>
/// Runs posted actions one at a time, in order, on whichever thread posts while nothing is
/// running; an action posted while another runs (from any thread, or from inside it) is run by
/// the thread already draining, after the current one. It has no thread of its own.
/// </summary>
internal sealed class SerialInbox
{
    private readonly Lock _gate = new();
    private readonly Queue<Action> _pending = new();
    private bool _draining;
    private int _failures;

    /// <summary>Inputs that threw. Always zero unless the coordinator has a bug.</summary>
    public int Failures => Volatile.Read(ref _failures);

    public void Post(Action action)
    {
        Enqueue(action);
        Drain();
    }

    public void Enqueue(Action action)
    {
        lock (_gate)
        {
            _pending.Enqueue(action);
        }
    }

    public void Drain()
    {
        lock (_gate)
        {
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out next!))
                {
                    _draining = false;
                    return;
                }
            }

            try
            {
                next();
            }
            catch (Exception)
            {
                // Actions are the coordinator's own continuations, which catch what they expect.
                // One that throws must not stop the inputs behind it from being processed; it is
                // counted, and the coordinator's tests require the count to stay at zero.
                Interlocked.Increment(ref _failures);
            }
        }
    }
}
