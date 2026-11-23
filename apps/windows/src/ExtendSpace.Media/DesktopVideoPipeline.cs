using System.Diagnostics;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Media;

public sealed record DesktopVideoPipelineOptions
{
    /// <summary>How often the pump looks round when nothing wakes it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// While a keyframe is owed and the encoder has produced none, how long after forcing one the
    /// pipeline forces again. On a still screen, the last captured picture is encoded again at this
    /// interval until a keyframe comes out; on a moving one, the next captured frame is forced.
    /// </summary>
    public TimeSpan KeyframeRetryInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Ask Windows for a 1 ms timer while capturing, as the experiment does (its measurements were made so).</summary>
    public bool RaiseTimerResolution { get; init; } = true;

    /// <summary>Receives one line per notable event: the source and encoder chosen, a fault, release.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// What the pipeline has done since it was last acquired. Cumulative; read at any time.
/// </summary>
/// <param name="CaptureCallbacks">Times Windows raised the frame-arrived event. With <paramref name="CaptureEmptyCallbacks"/> and <paramref name="FramesCaptured"/>, tells a still desktop (few callbacks, each with a frame) from starved callbacks.</param>
/// <param name="FramesCaptured">Frames Windows handed over and the pacer accepted, before the pump took them.</param>
/// <param name="FramesTaken">Frames the pump took from capture.</param>
/// <param name="FramesSubmitted">Frames given to the encoder, repeats included.</param>
/// <param name="FramesRepeated">Of those, the last captured picture encoded again because a keyframe was owed and the screen had not changed.</param>
/// <param name="FramesNotEncoded">Frames taken while encoding was off, or for which no encoder input surface was free.</param>
/// <param name="KeyframeRequests">Obligations raised through <see cref="IVideoPipeline.RequestKeyframe"/> while acquired.</param>
/// <param name="KeyframesForced">Times the encoder was told to make its next picture an IDR picture.</param>
/// <param name="AccessUnitsDelivered">Encoded access units handed to the sink.</param>
/// <param name="KeyframesDelivered">Of those, the ones containing an IDR picture: the moments an obligation was met.</param>
/// <param name="AccessUnitsDiscarded">Encoded access units not handed over because encoding had been turned off or the pipeline was stopping.</param>
/// <param name="FirstKeyframeAfterStart">From <see cref="IVideoPipeline.Start"/> to the first keyframe handed to the sink.</param>
public sealed record VideoPipelineCounters(
    long CaptureCallbacks,
    long CaptureEmptyCallbacks,
    long FramesCaptured,
    long FramesTaken,
    long FramesSubmitted,
    long FramesRepeated,
    long FramesNotEncoded,
    long KeyframeRequests,
    long KeyframesForced,
    long AccessUnitsDelivered,
    long KeyframesDelivered,
    long BytesDelivered,
    long AccessUnitsDiscarded,
    bool KeyframeOwed,
    TimeSpan? FirstKeyframeAfterStart,
    TimeSpan? SinceLastCapturedFrame);

/// <summary>
/// The production <see cref="IVideoPipeline"/>: a monitor captured with Windows.Graphics.Capture,
/// converted to NV12 on the GPU and encoded to H.264 with Media Foundation, the experiment's
/// measured path (architecture sections 2.2 and 6). The pump loop that the experiment ran in
/// <c>StreamRunner</c> is inside it, on a thread it owns from <see cref="Start"/> until it is
/// stopped.
///
/// Its life follows the contract: acquire (monitor, device, capture item; no encoder), prepare (an
/// encoder for exactly the negotiated stream), start, any number of encoding switches and keyframe
/// requests, stop. It can be acquired again after it is stopped.
///
/// <b>Keyframes.</b> A request makes a keyframe owed until an access unit containing an IDR picture
/// has been handed to the sink. Encoding a frame while one is owed forces it. If no new frame
/// arrives (a still screen), the last captured picture is kept, so it is encoded again, forced; if
/// no keyframe has come out <see cref="DesktopVideoPipelineOptions.KeyframeRetryInterval"/> after
/// forcing, it forces again. A request is therefore never satisfied by being asked for, only by a
/// keyframe reaching the sink. Keeping the last picture costs one of capture's three buffers, the
/// one the experiment used for the frame being converted, which this one also is.
///
/// <b>Starvation.</b> The pipeline counts (<see cref="Counters"/>); it does not judge. A capture
/// that delivers nothing is not a fault and nothing here restarts capture (decision D31).
///
/// <b>Release.</b> <see cref="StopAsync"/> stops frames reaching the sink at once, then the pump
/// stops capture and releases encoder, capture session, device and Media Foundation, in that order,
/// on its own thread; a call to acquire or prepare still running when the pipeline is stopped
/// releases what it made when it returns. The task <see cref="StopAsync"/> returns completes when
/// everything is released, whatever its cancellation token says, so that a caller that awaits it
/// never starts a new session beside an old capture session or encoder.
/// </summary>
public sealed class DesktopVideoPipeline : IVideoPipeline, IPipelineDiagnostics
{
    private const int MaxRememberedArrivals = 64;

    private readonly IMediaBackend _backend;
    private readonly DesktopVideoPipelineOptions _options;
    private readonly bool _ownPumpThread;
    private readonly Func<long> _timestamp;
    private readonly Lock _gate = new();

    // Under _gate. The acquisition the session is using, and the last one, for its counters and its release.
    private Acquisition? _current;
    private Acquisition? _last;

    public DesktopVideoPipeline(DesktopVideoPipelineOptions? options = null)
        : this(new DesktopMediaBackend(), options, ownPumpThread: true, timestamp: null)
    {
    }

    /// <param name="ownPumpThread">False for tests, which turn the pump by hand through <see cref="PumpOnce"/>.</param>
    /// <param name="timestamp">The <see cref="Stopwatch"/> clock, replaceable for tests.</param>
    internal DesktopVideoPipeline(IMediaBackend backend, DesktopVideoPipelineOptions? options, bool ownPumpThread, Func<long>? timestamp)
    {
        _backend = backend;
        _options = options ?? new DesktopVideoPipelineOptions();
        _ownPumpThread = ownPumpThread;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    /// <summary>The current acquisition's counters, or the last one's after it was stopped. Null before the first acquisition.</summary>
    public VideoPipelineCounters? Counters
    {
        get
        {
            Acquisition? acquisition;
            lock (_gate)
            {
                acquisition = _current ?? _last;
            }

            return acquisition?.Snapshot(_timestamp());
        }
    }

    /// <summary>The counters the stream health monitor reads (D67); null before the first acquisition.</summary>
    public PipelineDiagnosticCounters? DiagnosticCounters
    {
        get
        {
            Acquisition? acquisition;
            lock (_gate)
            {
                acquisition = _current;
            }

            if (acquisition?.Snapshot(_timestamp()) is not { } counters)
            {
                return null;
            }

            return new PipelineDiagnosticCounters(
                counters.FramesCaptured,
                counters.FramesSubmitted,
                counters.FramesRepeated,
                counters.AccessUnitsDelivered,
                counters.KeyframesForced,
                counters.KeyframeRequests,
                counters.SinceLastCapturedFrame,
                acquisition.TargetFrameRate,
                acquisition.ProducedStream);
        }
    }

    /// <summary>What was opened: monitor, adapter, encoder, its settings. Empty before the first acquisition.</summary>
    public IReadOnlyList<string> Details
    {
        get
        {
            lock (_gate)
            {
                return (_current ?? _last)?.Details.ToArray() ?? [];
            }
        }
    }

    /// <summary>
    /// The current or last stream as read from the encoder's own output (size, profile, level and any
    /// signalled frame rate), once its first keyframe has been checked against the configuration the
    /// client was sent (D57); null before then.
    /// </summary>
    public string? ProducedStream
    {
        get
        {
            lock (_gate)
            {
                return (_current ?? _last)?.ProducedStream;
            }
        }
    }

    /// <summary>Whether everything of every acquisition so far has been released.</summary>
    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _current is null && (_last is null || _last.Released.Task.IsCompleted);
            }
        }
    }

    // ---- The contract --------------------------------------------------------------------------

    public Task<AttemptOutcome<VideoSourceDescription>> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        Acquisition acquisition;
        Task previousRelease;
        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("The pipeline is already acquired; stop it first.");
            }

            previousRelease = _last?.Released.Task ?? Task.CompletedTask;
            acquisition = new Acquisition(this) { Busy = true };
            _current = acquisition;
            _last = acquisition;
        }

        return Run(async () =>
        {
            // A source is never opened beside a previous one that is still letting go.
            await previousRelease.WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();

            var wake = new WakeSignal();
            ICaptureSource source;
            try
            {
                source = _backend.OpenSource(request, wake.Set);
            }
            catch (Exception exception)
            {
                wake.Dispose();
                Finish(acquisition, keep: false);
                return AttemptOutcome<VideoSourceDescription>.Failed(SourceFault(exception));
            }

            lock (_gate)
            {
                acquisition.Wake = wake;
                acquisition.Source = source;
            }

            if (!Finish(acquisition, keep: !cancellation.IsCancellationRequested))
            {
                return AttemptOutcome<VideoSourceDescription>.Cancelled;
            }

            lock (_gate)
            {
                acquisition.Details.AddRange(source.Details);
            }

            Log($"display acquired: {source.Description.Width} x {source.Description.Height} at {source.Description.RefreshHz:0.##} Hz");
            return AttemptOutcome<VideoSourceDescription>.Succeeded(source.Description);
        }, () => Finish(acquisition, keep: false), AttemptOutcome<VideoSourceDescription>.Cancelled);
    }

    public Task<AttemptOutcome<StreamSettings>> PrepareAsync(EncodingRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        Acquisition acquisition;
        lock (_gate)
        {
            acquisition = _current is { Source: not null, Encoder: null, Busy: false, Stopping: false } current
                ? current
                : throw new InvalidOperationException("The pipeline has to be acquired, and not yet prepared, before it is prepared.");
            acquisition.Busy = true;
        }

        return Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            IFrameEncoder encoder;
            try
            {
                encoder = acquisition.Source!.OpenEncoder(request, acquisition.OnEncoded, acquisition.Wake!.Set);
                acquisition.TargetFrameRate = request.FrameRate;
            }
            catch (Exception exception)
            {
                Finish(acquisition, keep: true);
                return Task.FromResult(AttemptOutcome<StreamSettings>.Failed(EncoderFault(exception)));
            }

            lock (_gate)
            {
                acquisition.Encoder = encoder;
            }

            if (!Finish(acquisition, keep: !cancellation.IsCancellationRequested, undo: () => acquisition.DropEncoder()))
            {
                return Task.FromResult(AttemptOutcome<StreamSettings>.Cancelled);
            }

            lock (_gate)
            {
                acquisition.Details.AddRange(encoder.Details);
            }

            StreamSettings settings = encoder.Settings;
            Log($"encoder prepared: {settings.Width} x {settings.Height} at {settings.FrameRate} fps, {settings.BitrateBitsPerSecond} bit/s, '{settings.EncoderName}' ({(settings.EncoderIsHardware ? "hardware" : "not confirmed as hardware")})");
            return Task.FromResult(AttemptOutcome<StreamSettings>.Succeeded(settings));
        }, () => Finish(acquisition, keep: true), AttemptOutcome<StreamSettings>.Cancelled);
    }

    public void Start(EncodedVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Acquisition acquisition;
        lock (_gate)
        {
            acquisition = _current is { Encoder: not null, Busy: false, Stopping: false, Started: false } current
                ? current
                : throw new InvalidOperationException("The pipeline has to be prepared, and not yet started, before it is started.");
            acquisition.Started = true;
            acquisition.Sink = sink;
            acquisition.StartedAt = _timestamp();

            // Published under the lock, so that a stop sees that the pump owns the release.
            if (_ownPumpThread)
            {
                acquisition.PumpThread = new Thread(acquisition.Pump)
                {
                    IsBackground = true,
                    Name = "ExtendSpace video pipeline",
                };
            }
        }

        if (acquisition.PumpThread is { } thread)
        {
            thread.Start();
        }
        else
        {
            acquisition.BeginCapture();
        }
    }

    public void SetEncoding(bool enabled)
    {
        Acquisition? acquisition = Volatile.Read(ref _current);
        if (acquisition is null)
        {
            return;
        }

        acquisition.Encoding = enabled;
        acquisition.Wake?.Set();
    }

    public void RequestKeyframe()
    {
        Acquisition? acquisition = Volatile.Read(ref _current);
        if (acquisition is not { Source: not null, Stopping: false })
        {
            return;
        }

        long now = _timestamp();
        long requests = Interlocked.Increment(ref acquisition.KeyframeRequestCount);

        // Diagnostics (D62): the first request of an obligation is logged; the rest merge into it.
        bool opens = Interlocked.CompareExchange(ref acquisition.KeyframeOwedSince, now, -1) == -1;
        Volatile.Write(ref acquisition.KeyframeOwed, 1);
        acquisition.Wake?.Set();
        if (opens)
        {
            Log($"keyframe pipeline_owed t={ToMicros(now)} requests={requests}");
        }
    }

    public Task<SessionFault?> WaitForFaultAsync(CancellationToken cancellation)
    {
        Acquisition? acquisition;
        lock (_gate)
        {
            acquisition = _current;
        }

        return acquisition is null
            ? Task.FromResult<SessionFault?>(null)
            : acquisition.Fault.Task.WaitAsync(cancellation);
    }

    public Task StopAsync(CancellationToken cancellation)
    {
        Acquisition? acquisition;
        bool releaseNow;
        lock (_gate)
        {
            acquisition = _current;
            _current = null;
            if (acquisition is null)
            {
                return _last?.Released.Task ?? Task.CompletedTask;
            }

            // From here no frame reaches the sink, and a call still running releases what it made.
            acquisition.Stopping = true;
            acquisition.Encoding = false;
            releaseNow = !acquisition.Busy && acquisition.PumpThread is null;
        }

        acquisition.Fault.TrySetResult(null);
        if (releaseNow)
        {
            // Nothing else is touching the acquisition: release it off the caller's thread.
            _ = Task.Run(acquisition.Release);
        }
        else
        {
            // The pump, or the call in progress, releases it when it sees the stop.
            acquisition.Wake?.Set();
        }

        // Deliberately not cut short by the token: what the caller would get back early is a task
        // saying "stopped" while the capture session and the encoder are still alive.
        return acquisition.Released.Task;
    }

    /// <summary>For tests without a pump thread: one turn of the loop. False once the pump has ended.</summary>
    internal bool PumpOnce()
    {
        Acquisition? acquisition;
        lock (_gate)
        {
            acquisition = _last;
        }

        if (acquisition is null || _ownPumpThread || !acquisition.Started || acquisition.Released.Task.IsCompleted)
        {
            return false;
        }

        if (acquisition.Stopping || !acquisition.Turn())
        {
            acquisition.Release();
            return false;
        }

        return true;
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    /// <summary>
    /// Ends an acquire or prepare call. True if what it made is kept; false if the pipeline was
    /// stopped meanwhile, or the call was cancelled, in which case it is undone: a prepare's encoder
    /// is closed, and an acquisition that was stopped is released whole.
    /// </summary>
    private bool Finish(Acquisition acquisition, bool keep, Action? undo = null)
    {
        bool stopped;
        lock (_gate)
        {
            acquisition.Busy = false;
            stopped = acquisition.Stopping;
            if (!stopped && !keep && acquisition.Source is null)
            {
                // A failed or cancelled acquire leaves the pipeline as it found it.
                _current = null;
                acquisition.Stopping = true;
            }
        }

        if (stopped)
        {
            acquisition.Release();
            return false;
        }

        if (!keep)
        {
            if (acquisition.Source is null)
            {
                acquisition.Release();
            }
            else
            {
                undo?.Invoke();
                if (undo is null)
                {
                    // A cancelled acquire that did open its source: drop it as if it never happened.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_current, acquisition))
                        {
                            _current = null;
                        }

                        acquisition.Stopping = true;
                    }

                    acquisition.Release();
                }
            }

            return false;
        }

        return true;
    }

    /// <summary>Runs a blocking acquire or prepare on the thread pool; cancellation observed by the body is reported as such.</summary>
    private static Task<T> Run<T>(Func<Task<T>> body, Action onCancelled, T cancelled)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(async () =>
        {
            try
            {
                done.TrySetResult(await body().ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                onCancelled();
                done.TrySetResult(cancelled);
            }
            catch (Exception exception)
            {
                onCancelled();
                done.TrySetException(exception);
            }
        });
        return done.Task;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private static SessionFault SourceFault(Exception exception) => exception is MediaSourceException source
        ? new SessionFault(FaultCategory.Capture, source.Code, source.Message, FaultTreatment.Terminal)
        : new SessionFault(FaultCategory.Capture, MediaFaultCodes.CaptureUnavailable, $"The display could not be acquired: {Describe(exception)}", FaultTreatment.Terminal);

    private static SessionFault EncoderFault(Exception exception) => exception is MediaEncoderException encoder
        ? new SessionFault(FaultCategory.Encoder, encoder.Code, encoder.Message, FaultTreatment.Terminal)
        : new SessionFault(FaultCategory.Encoder, MediaFaultCodes.EncoderUnavailable, $"No encoder could be opened: {Describe(exception)}", FaultTreatment.Terminal);

    internal static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message.Trim()} (0x{exception.HResult:X8})";

    private static long ToMicros(long timestamp) => (long)((Int128)timestamp * 1_000_000 / Stopwatch.Frequency);

    private static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        foreach (NalUnit unit in AnnexB.FindNalUnits(accessUnit))
        {
            if (AnnexB.NalType(accessUnit[unit.Offset]) == H264NalType.IdrSlice)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Everything one acquisition owns, from acquire to release. Fields are written under the
    /// pipeline's lock until the pump starts; after that the pump thread owns the source, the
    /// encoder and the last frame, and the other threads touch only the flags.
    /// </summary>
    private sealed class Acquisition(DesktopVideoPipeline owner)
    {
        private readonly Dictionary<long, long> _arrivals = [];
        private int _released;

        public readonly TaskCompletionSource<SessionFault?> Fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Details = [];

        /// <summary>What the encoder was read to produce, from its first checked keyframe (D57).</summary>
        public volatile string? ProducedStream;
        public volatile int TargetFrameRate;

        public ICaptureSource? Source;
        public IFrameEncoder? Encoder;
        public WakeSignal? Wake;
        public Thread? PumpThread;
        public EncodedVideoSink? Sink;
        public bool Busy;
        public bool Started;
        public long StartedAt;
        public volatile bool Stopping;
        public volatile bool Encoding;
        public int KeyframeOwed;

        /// <summary>Diagnostics only (D62): when the obligation now owed was opened; -1 when none is.</summary>
        public long KeyframeOwedSince = -1;

        // Pump thread only.
        private ICapturedFrame? _lastFrame;
        private bool _freshFrame;
        private long? _forcedAt;

        // Pump thread only, diagnostics (D62): forces made for the obligation now owed, and the first.
        private int _obligationForces;
        private long _obligationFirstForce;
        private long _timeOrigin = -1;
        private long _lastSampleTime = -1;
        private TimerResolution? _timer;

        // Counters: written by the pump thread, read by anyone.
        public long KeyframeRequestCount;
        private long _taken;
        private long _submitted;
        private long _repeated;
        private long _notEncoded;
        private long _forced;
        private long _delivered;
        private long _keyframesDelivered;
        private long _bytesDelivered;
        private long _discarded;
        private long _firstKeyframeAt = -1;

        /// <summary>The pump thread's body.</summary>
        public void Pump()
        {
            try
            {
                if (owner._options.RaiseTimerResolution)
                {
                    _timer = TimerResolution.Request();
                }

                BeginCapture();
                while (!Stopping)
                {
                    Wake!.Wait(owner._options.PollInterval);
                    if (Stopping || !Turn())
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                // Starting capture failed: Turn reports its own failures.
                Fail(new SessionFault(FaultCategory.Capture, MediaFaultCodes.CaptureFailed, $"Capture could not be started: {Describe(exception)}", FaultTreatment.Terminal));
            }
            finally
            {
                Release();
            }
        }

        public void BeginCapture() => Source!.Start();

        /// <summary>One turn of the loop. False when the pipeline has failed and the pump must end.</summary>
        public bool Turn()
        {
            try
            {
                if (Source!.Failure is { } failure)
                {
                    Fail(new SessionFault(FaultCategory.Capture, MediaFaultCodes.CaptureFailed, failure, FaultTreatment.Terminal));
                    return false;
                }

                IFrameEncoder encoder = Encoder!;
                encoder.ProcessEvents();

                bool encoding = Encoding;
                while (Source.TryTake(out ICapturedFrame? frame))
                {
                    // The newest picture is always kept, encoding or not, so that a keyframe can be
                    // made from it whenever one is owed and the screen does not change.
                    Interlocked.Increment(ref _taken);
                    if (_freshFrame || !encoding)
                    {
                        Interlocked.Increment(ref _notEncoded);
                    }

                    _lastFrame?.Dispose();
                    _lastFrame = frame;
                    _freshFrame = encoding;
                }

                if (!encoding || _lastFrame is null || !encoder.CanAcceptInput)
                {
                    return true;
                }

                long now = owner._timestamp();
                bool owed = Volatile.Read(ref KeyframeOwed) != 0;
                bool retryDue = _forcedAt is not { } forcedAt || now - forcedAt >= ToTimestamp(owner._options.KeyframeRetryInterval);
                if (_freshFrame)
                {
                    if (owed && retryDue)
                    {
                        Force(encoder, now);
                    }

                    if (Submit(encoder, _lastFrame, _lastFrame.Arrival))
                    {
                        _freshFrame = false;
                    }
                }
                else if (owed && retryDue)
                {
                    // A still screen: nothing new will arrive, so the picture already shown is encoded
                    // again as a keyframe.
                    Force(encoder, now);
                    if (Submit(encoder, _lastFrame, now))
                    {
                        Interlocked.Increment(ref _repeated);
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                Fail(exception is MediaEncoderException encoderFailure
                    ? new SessionFault(FaultCategory.Encoder, encoderFailure.Code, encoderFailure.Message, FaultTreatment.Terminal)
                    : new SessionFault(FaultCategory.Encoder, MediaFaultCodes.EncoderFailed, $"Encoding failed: {Describe(exception)}", FaultTreatment.Terminal));
                return false;
            }
        }

        /// <summary>The encoder's output handler, on the pump thread.</summary>
        public void OnEncoded(ReadOnlySpan<byte> data, long sampleTime)
        {
            long captureMicros = _arrivals.Remove(sampleTime, out long arrival) ? ToMicros(arrival) : MonotonicClock.NowMicros;
            EncodedVideoSink? sink = Sink;
            if (Stopping || !Encoding || sink is null)
            {
                // Encoding was turned off after the frame went in: it is not sent, and it does not
                // count as a keyframe delivered, because nobody received it.
                Interlocked.Increment(ref _discarded);
                return;
            }

            if (ContainsIdr(data))
            {
                Volatile.Write(ref KeyframeOwed, 0);
                _forcedAt = null;
                TraceObligationMet();
                Interlocked.Increment(ref _keyframesDelivered);
                if (Interlocked.Read(ref _firstKeyframeAt) < 0)
                {
                    Interlocked.Exchange(ref _firstKeyframeAt, owner._timestamp());
                }

                if (ProducedStream is null && Encoder?.ProducedStream is { } produced)
                {
                    ProducedStream = produced;
                    owner.Log($"encoder output checked against the stream configuration: {produced}");
                }
            }

            Interlocked.Increment(ref _delivered);
            Interlocked.Add(ref _bytesDelivered, data.Length);
            sink(data, captureMicros);
        }

        public void DropEncoder()
        {
            IFrameEncoder? encoder;
            lock (owner._gate)
            {
                encoder = Encoder;
                Encoder = null;
            }

            encoder?.Dispose();
        }

        public void Fail(SessionFault fault)
        {
            if (Fault.TrySetResult(fault))
            {
                owner.Log($"pipeline failed: {fault.Code}: {fault.Message}");
            }
        }

        /// <summary>Releases everything, once, in the reverse of the order it was made in.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            Stopping = true;
            var problems = new List<string>();
            void Try(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    problems.Add($"{what}: {Describe(exception)}");
                }
            }

            if (Source is { } source)
            {
                Try("stopping capture", source.Stop);
            }

            Try("releasing the last frame", () => _lastFrame?.Dispose());
            _lastFrame = null;
            Try("closing the encoder", () => Encoder?.Dispose());
            Try("releasing the display", () => Source?.Dispose());
            Try("restoring the timer resolution", () => _timer?.Dispose());
            Wake?.Dispose();

            lock (owner._gate)
            {
                if (ReferenceEquals(owner._current, this))
                {
                    owner._current = null;
                }
            }

            owner.Log(problems.Count == 0 ? "pipeline released" : $"pipeline released with problems: {string.Join("; ", problems)}");
            Fault.TrySetResult(null);
            Released.TrySetResult();
        }

        public VideoPipelineCounters Snapshot(long now)
        {
            CaptureActivity activity = Source?.Activity ?? CaptureActivity.None;
            long firstKeyframe = Interlocked.Read(ref _firstKeyframeAt);
            return new VideoPipelineCounters(
                activity.Callbacks,
                activity.EmptyCallbacks,
                activity.Delivered,
                Interlocked.Read(ref _taken),
                Interlocked.Read(ref _submitted),
                Interlocked.Read(ref _repeated),
                Interlocked.Read(ref _notEncoded),
                Interlocked.Read(ref KeyframeRequestCount),
                Interlocked.Read(ref _forced),
                Interlocked.Read(ref _delivered),
                Interlocked.Read(ref _keyframesDelivered),
                Interlocked.Read(ref _bytesDelivered),
                Interlocked.Read(ref _discarded),
                Volatile.Read(ref KeyframeOwed) != 0,
                firstKeyframe >= 0 && Started ? Stopwatch.GetElapsedTime(StartedAt, firstKeyframe) : null,
                activity.LastArrival is { } last ? Stopwatch.GetElapsedTime(last, now) : null);
        }

        private void Force(IFrameEncoder encoder, long now)
        {
            encoder.ForceKeyframe();
            Interlocked.Increment(ref _forced);
            _forcedAt = now;

            // Diagnostics (D62): only the first force of an obligation is logged; retries are counted.
            if (_obligationForces++ == 0)
            {
                _obligationFirstForce = now;
                long since = Interlocked.Read(ref KeyframeOwedSince);
                owner.Log($"keyframe pipeline_forced t={ToMicros(now)} since_owed_ms={(since >= 0 ? Ms(now - since) : "unknown")}");
            }
        }

        /// <summary>
        /// Diagnostics (D62), on the pump thread: an IDR access unit reached the sink. A line only if
        /// an obligation was open; it was met by this keyframe whether or not it was forced for it
        /// (<c>forced=0</c>: an earlier force's or the encoder's own periodic keyframe).
        /// </summary>
        private void TraceObligationMet()
        {
            long since = Interlocked.Exchange(ref KeyframeOwedSince, -1);
            int forces = _obligationForces;
            _obligationForces = 0;
            if (since < 0)
            {
                return;
            }

            long now = owner._timestamp();
            string sinceForce = forces > 0 ? Ms(now - _obligationFirstForce) : "none";
            owner.Log($"keyframe pipeline_met t={ToMicros(now)} since_owed_ms={Ms(now - since)} since_first_force_ms={sinceForce} forced={forces} requests_total={Interlocked.Read(ref KeyframeRequestCount)}");
        }

        private static string Ms(long timestamp) =>
            ((double)timestamp * 1000 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private bool Submit(IFrameEncoder encoder, ICapturedFrame frame, long at)
        {
            // Sample times count from the first frame, on the hand-over clock, and only go forward:
            // a picture encoded again gets the time it was encoded again.
            if (_timeOrigin < 0)
            {
                _timeOrigin = at;
            }

            long sampleTime = Math.Max(_lastSampleTime + 1, ToTicks(at - _timeOrigin));

            // Remembered before submitting: a synchronous encoder delivers its output inside Submit.
            if (_arrivals.Count >= MaxRememberedArrivals)
            {
                _arrivals.Remove(_arrivals.Keys.Min());
            }

            _arrivals[sampleTime] = at;
            if (!encoder.Submit(frame, sampleTime))
            {
                _arrivals.Remove(sampleTime);
                Interlocked.Increment(ref _notEncoded);
                return false;
            }

            _lastSampleTime = sampleTime;
            Interlocked.Increment(ref _submitted);
            return true;
        }

        private static long ToTicks(long timestampDelta) => (long)((Int128)timestampDelta * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

        private static long ToTimestamp(TimeSpan interval) => (long)((Int128)interval.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }
}

/// <summary>Fault codes the video pipeline raises. All are terminal (decision D34).</summary>
public static class MediaFaultCodes
{
    /// <summary>Capture: Windows.Graphics.Capture is not available in this session.</summary>
    public const string CaptureUnsupported = "capture_unsupported";

    /// <summary>Capture: the requested monitor does not exist.</summary>
    public const string MonitorNotFound = "monitor_not_found";

    /// <summary>Capture: the display, the Direct3D device or the capture item could not be created.</summary>
    public const string CaptureUnavailable = "capture_unavailable";

    /// <summary>Capture: the source became unusable while capturing (monitor gone, size changed), or capture would not start.</summary>
    public const string CaptureFailed = "capture_failed";

    /// <summary>Encoder: no H.264 encoder could be opened for the chosen stream.</summary>
    public const string EncoderUnavailable = "encoder_unavailable";

    /// <summary>Encoder: the source is too small to encode.</summary>
    public const string SourceTooSmall = "source_too_small";

    /// <summary>Encoder: the encoder or the conversion in front of it failed while running.</summary>
    public const string EncoderFailed = "encoder_failed";

    /// <summary>Encoder: what the encoder produced is not the stream the client was configured for (D57).</summary>
    public const string EncoderOutputMismatch = "encoder_output_mismatch";
}
