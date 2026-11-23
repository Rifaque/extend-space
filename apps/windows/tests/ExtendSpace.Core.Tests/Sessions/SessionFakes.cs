using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

// Scripted substitutes for the coordinator's seams. By default every call succeeds at once; a test
// replaces the delegate for the call it is about. Every call is written to one shared log, with
// the state machine's phases, so that tests can assert the order of things across components.
// The fakes complete their tasks inline on the thread that finishes them, which is the test's
// thread: that is what makes the coordinator run on the test's thread.

internal sealed class CallLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public int Count(string entry) => Entries.Count(candidate => candidate == entry);

    /// <summary>The entries from the first occurrence of <paramref name="entry"/> on.</summary>
    public IReadOnlyList<string> From(string entry) => [.. Entries.SkipWhile(candidate => candidate != entry)];

    public int IndexOf(string entry) => Entries.ToList().IndexOf(entry);
}

/// <summary>Ready-made replies.</summary>
internal static class Reply
{
    public static Task<AttemptOutcome> Ok() => Task.FromResult(AttemptOutcome.Succeeded);

    public static Task<AttemptOutcome<T>> Ok<T>(T value)
        where T : class => Task.FromResult(AttemptOutcome<T>.Succeeded(value));

    public static Task<AttemptOutcome> Fail(SessionFault fault) => Task.FromResult(AttemptOutcome.Failed(fault));

    public static Task<AttemptOutcome<T>> Fail<T>(SessionFault fault)
        where T : class => Task.FromResult(AttemptOutcome<T>.Failed(fault));

    /// <summary>A call that waits until cancelled and then reports it, as the contract requires.</summary>
    public static Task<AttemptOutcome> UntilCancelled(CancellationToken token)
    {
        var source = new TaskCompletionSource<AttemptOutcome>();
        token.Register(() => source.TrySetResult(AttemptOutcome.Cancelled));
        return source.Task;
    }

    public static Task<AttemptOutcome<T>> UntilCancelled<T>(CancellationToken token)
        where T : class
    {
        var source = new TaskCompletionSource<AttemptOutcome<T>>();
        token.Register(() => source.TrySetResult(AttemptOutcome<T>.Cancelled));
        return source.Task;
    }
}

internal static class Faults
{
    public static readonly SessionFault NothingListening = new(FaultCategory.Connection, "no_app_listening", "Nothing is listening on the device.", FaultTreatment.Retryable);
    public static readonly SessionFault Reset = new(FaultCategory.Connection, "connection_reset", "The connection was reset.", FaultTreatment.Retryable);
    public static readonly SessionFault HandshakeTimeout = new(FaultCategory.Timeout, "handshake_timeout", "Nothing answered within 5 s.", FaultTreatment.Retryable);
    public static readonly SessionFault AdbMissing = new(FaultCategory.Transport, "adb_not_found", "adb was not found.", FaultTreatment.Terminal, UserActionable: true);
    public static readonly SessionFault VersionMismatch = new(FaultCategory.Protocol, SessionFaultCodes.ProtocolVersionUnsupported, "The device speaks another protocol version.", FaultTreatment.Terminal);
    public static readonly SessionFault StreamRejected = new(FaultCategory.Protocol, SessionFaultCodes.StreamConfigRejected, "The device cannot decode the stream.", FaultTreatment.Terminal);
    public static readonly SessionFault Malformed = new(FaultCategory.Protocol, "malformed_message", "The device sent a malformed message.", FaultTreatment.Terminal);
    public static readonly SessionFault MonitorGone = new(FaultCategory.Capture, "monitor_not_found", "The monitor is not attached.", FaultTreatment.Terminal);
    public static readonly SessionFault MonitorResized = new(FaultCategory.Capture, "monitor_resized", "The monitor changed size.", FaultTreatment.Terminal);

    // Marked retryable on purpose: the coordinator must still treat a pipeline fault as terminal.
    public static readonly SessionFault NoEncoder = new(FaultCategory.Encoder, "encoder_unavailable", "No encoder could be opened.", FaultTreatment.Retryable);
}

internal sealed class FakeConnection(CallLog log) : ISessionConnection
{
    public static readonly ClientCapabilities Tablet = new("Pad", "0.1.0", 3200, 2136, 144.0, [new DecoderCapability("c2.hw.avc", true, true, 4096, 4096)]);
    public static readonly ClientCapabilities SmallDecoderTablet = Tablet with { Decoders = [new DecoderCapability("c2.sw.avc", false, false, 1280, 720)] };
    public static readonly ClientCapabilities TinyDecoderTablet = Tablet with { Decoders = [new DecoderCapability("c2.sw.avc", false, false, 640, 480)] };
    public static readonly PeerDescription Peer = new("Pad", "0.1.0", "c2.hw.avc", true);

    private TaskCompletionSource<ConnectionEnd>? _end;
    private bool _configured;
    private Task? _previous;

    public Func<int, CancellationToken, Task<AttemptOutcome>> Open { get; set; } = (_, _) => Reply.Ok();

    public Func<int, CancellationToken, Task<AttemptOutcome<ClientCapabilities>>> Exchange { get; set; } = (_, _) => Reply.Ok(Tablet);

    public Func<StreamSettings, CancellationToken, Task<AttemptOutcome<PeerDescription>>> Configure { get; set; } = (_, _) => Reply.Ok(Peer);

    public Func<CancellationToken, Task> Close { get; set; } = _ => Task.CompletedTask;

    public int Opens { get; private set; }

    public int Exchanges { get; private set; }

    public int Configures { get; private set; }

    public int Closes { get; private set; }

    public int FramesOffered { get; private set; }

    /// <summary>Calls started while another call of this connection was still running. The coordinator promises none.</summary>
    public int Overlaps { get; private set; }

    public (string Reason, SessionFault? Fault)? LastClose { get; private set; }

    public StreamSettings? LastConfigured { get; private set; }

    public bool IsConfigured => _configured;

    public event Action? KeyframeWanted;

    public Task<AttemptOutcome> OpenAsync(CancellationToken cancellation)
    {
        log.Add("connection.Open");
        Opens++;

        // A new attempt discards whatever connection was there.
        _configured = false;
        return Track(Open(Opens, cancellation));
    }

    public Task<AttemptOutcome<ClientCapabilities>> ExchangeCapabilitiesAsync(CancellationToken cancellation)
    {
        log.Add("connection.Exchange");
        Exchanges++;
        return Track(Exchange(Exchanges, cancellation));
    }

    public Task<AttemptOutcome<PeerDescription>> ConfigureStreamAsync(StreamSettings stream, CancellationToken cancellation)
    {
        log.Add($"connection.Configure {stream.Width}x{stream.Height}");
        Configures++;
        LastConfigured = stream;
        Task<AttemptOutcome<PeerDescription>> reply = Track(Configure(stream, cancellation));
        reply.ContinueWith(
            completed => _configured = completed.IsCompletedSuccessfully && completed.Result.Kind == OutcomeKind.Succeeded,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return reply;
    }

    public Task<ConnectionEnd> WaitForEndAsync(CancellationToken cancellation)
    {
        // The contract (gap G9): there is nothing to wait for before a stream is configured.
        if (!_configured)
        {
            throw new InvalidOperationException("WaitForEndAsync was called before a stream was configured.");
        }

        log.Add("connection.WaitForEnd");
        _end = new TaskCompletionSource<ConnectionEnd>();
        TaskCompletionSource<ConnectionEnd> end = _end;
        cancellation.Register(() => end.TrySetCanceled(cancellation));
        return end.Task;
    }

    /// <summary>Ends the configured connection as the real one would.</summary>
    public void End(ConnectionEnd end)
    {
        log.Add($"connection ended: {end.Kind}");
        _configured = false;
        _end!.SetResult(end);
    }

    public void OfferVideo(ReadOnlySpan<byte> accessUnit, long captureMicros) => FramesOffered++;

    public void RaiseKeyframeWanted() => KeyframeWanted?.Invoke();

    public Task CloseAsync(string reason, SessionFault? fault, CancellationToken cancellation)
    {
        log.Add("connection.Close");
        Closes++;
        LastClose = (reason, fault);
        _configured = false;
        _end?.TrySetResult(new ConnectionEnd(ConnectionEndKind.Closed));
        return Close(cancellation);
    }

    // Whether the previous call had finished when this one began. Its task's completion is
    // recorded before any continuation runs, so this cannot race with the coordinator, which only
    // moves on after seeing that completion. (Counting in a continuation of our own did race: that
    // continuation can run on another thread after the coordinator's.)
    private Task<T> Track<T>(Task<T> call)
    {
        if (_previous is { IsCompleted: false })
        {
            Overlaps++;
        }

        _previous = call;
        return call;
    }
}

internal sealed class FakePipeline(CallLog log) : IVideoPipeline
{
    public static readonly VideoSourceDescription Monitor = new(2560, 1440, 60.0);

    private TaskCompletionSource<SessionFault?>? _fault;
    private Task? _previous;

    public Func<CancellationToken, Task<AttemptOutcome<VideoSourceDescription>>> Acquire { get; set; } = _ => Reply.Ok(Monitor);

    public Func<EncodingRequest, CancellationToken, Task<AttemptOutcome<StreamSettings>>> Prepare { get; set; } =
        (request, _) => Reply.Ok(new StreamSettings(request.Width, request.Height, request.FrameRate, request.BitrateBitsPerSecond, "hardware encoder", true));

    public Func<CancellationToken, Task> Stop { get; set; } = _ => Task.CompletedTask;

    public int Acquires { get; private set; }

    public int Prepares { get; private set; }

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public int KeyframeRequests { get; private set; }

    public bool Encoding { get; private set; }

    /// <summary>
    /// Models the contract's standing obligation: set by a request, cleared only when a keyframe
    /// is produced, which needs encoding on and a picture (<see cref="Capture"/>).
    /// </summary>
    public bool KeyframeOwed { get; private set; }

    public EncodedVideoSink? Sink { get; private set; }

    public int Overlaps { get; private set; }

    public Task<AttemptOutcome<VideoSourceDescription>> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation)
    {
        log.Add("pipeline.Acquire");
        Acquires++;
        return Track(Acquire(cancellation));
    }

    public Task<AttemptOutcome<StreamSettings>> PrepareAsync(EncodingRequest request, CancellationToken cancellation)
    {
        log.Add($"pipeline.Prepare {request.Width}x{request.Height}@{request.FrameRate}");
        Prepares++;
        return Track(Prepare(request, cancellation));
    }

    public void Start(EncodedVideoSink sink)
    {
        log.Add("pipeline.Start");
        Starts++;
        Sink = sink;
    }

    public void SetEncoding(bool enabled)
    {
        log.Add(enabled ? "pipeline.Encoding on" : "pipeline.Encoding off");
        Encoding = enabled;
    }

    public void RequestKeyframe()
    {
        log.Add("pipeline.Keyframe");
        KeyframeRequests++;
        KeyframeOwed = true;
    }

    /// <summary>One picture captured. Encoded only while encoding is on; a keyframe if one is owed.</summary>
    public void Capture()
    {
        if (Encoding && Sink is { } sink)
        {
            KeyframeOwed = false;
            sink([0, 0, 0, 1, 0x65], 0);
        }
    }

    public Task<SessionFault?> WaitForFaultAsync(CancellationToken cancellation)
    {
        log.Add("pipeline.WatchFault");
        _fault = new TaskCompletionSource<SessionFault?>();
        TaskCompletionSource<SessionFault?> fault = _fault;
        cancellation.Register(() => fault.TrySetCanceled(cancellation));
        return fault.Task;
    }

    public void Fail(SessionFault fault)
    {
        log.Add($"pipeline failed: {fault.Code}");
        _fault!.SetResult(fault);
    }

    public Task StopAsync(CancellationToken cancellation)
    {
        log.Add("pipeline.Stop");
        Stops++;
        Encoding = false;
        _fault?.TrySetResult(null);
        return Stop(cancellation);
    }

    // Whether the previous call had finished when this one began. Its task's completion is
    // recorded before any continuation runs, so this cannot race with the coordinator, which only
    // moves on after seeing that completion. (Counting in a continuation of our own did race: that
    // continuation can run on another thread after the coordinator's.)
    private Task<T> Track<T>(Task<T> call)
    {
        if (_previous is { IsCompleted: false })
        {
            Overlaps++;
        }

        _previous = call;
        return call;
    }
}

/// <summary>A coordinator with fakes, a manual clock and a log of everything that happened.</summary>
internal sealed class CoordinatorHarness
{
    public static readonly SessionRequest UsbMirror = new(TransportKind.Usb, DisplayMode.Mirror);

    public CoordinatorHarness(SessionCoordinatorOptions? options = null)
    {
        Connection = new FakeConnection(Log);
        Pipeline = new FakePipeline(Log);
        Machine = new SessionStateMachine();
        Coordinator = new SessionCoordinator(Machine, Connection, Pipeline, Clock, options);
        Coordinator.StateChanged += (_, _) =>
        {
            States.Add(Coordinator.State);
            Log.Add($"state {Coordinator.State.Phase}");
        };
    }

    public CallLog Log { get; } = new();

    public ManualSessionClock Clock { get; } = new();

    public FakeConnection Connection { get; }

    public FakePipeline Pipeline { get; }

    public SessionStateMachine Machine { get; }

    public SessionCoordinator Coordinator { get; }

    public List<SessionState> States { get; } = [];

    public SessionState State => Coordinator.State;

    public SessionPhase Phase => Coordinator.State.Phase;

    public IEnumerable<SessionPhase> Phases => States.Select(state => state.Phase);

    public SessionStartResult Start() => Coordinator.Start(UsbMirror);

    /// <summary>Starts and checks that the session reached Streaming at once.</summary>
    public CoordinatorHarness Streaming()
    {
        Assert.Equal(SessionStartResult.Started, Start());
        Assert.Equal(SessionPhase.Streaming, Phase);
        return this;
    }

    /// <summary>What every test can require at its end: no timer left, nothing thrown inside the coordinator, no overlapping calls.</summary>
    public void AssertSettled()
    {
        Assert.True(Clock.PendingDelays == 0, $"{Clock.PendingDelays} clock delay(s) still pending");
        Assert.True(Coordinator.UnhandledInputFailures == 0, $"{Coordinator.UnhandledInputFailures} coordinator input(s) threw");
        Assert.True(Connection.Overlaps == 0, $"{Connection.Overlaps} overlapping connection call(s)");
        Assert.True(Pipeline.Overlaps == 0, $"{Pipeline.Overlaps} overlapping pipeline call(s)");
    }
}
