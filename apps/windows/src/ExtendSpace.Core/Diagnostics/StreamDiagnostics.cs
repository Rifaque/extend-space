using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Diagnostics;

/// <summary>The pipeline's counters as the diagnostics need them; read from any thread.</summary>
/// <param name="TargetFrameRate">The frame rate the encoder was prepared for; 0 before it was.</param>
/// <param name="Stream">What the encoder produced, as read from its own output (D57); null before the first keyframe.</param>
public sealed record PipelineDiagnosticCounters(
    long FramesCaptured,
    long FramesSubmitted,
    long FramesRepeated,
    long FramesEncoded,
    long KeyframesForced,
    long KeyframeRequests,
    TimeSpan? SinceLastCapturedFrame,
    int TargetFrameRate,
    string? Stream);

/// <summary>Implemented by a pipeline that can report its counters (the desktop pipeline does).</summary>
public interface IPipelineDiagnostics
{
    /// <summary>The current acquisition's counters; null when there is none.</summary>
    PipelineDiagnosticCounters? DiagnosticCounters { get; }
}

/// <summary>One diagnostics reading: the health classification and the raw figures behind it.</summary>
public sealed record StreamDiagnosticsSnapshot(
    SessionPhase Phase,
    StreamHealthReport Health,
    PipelineDiagnosticCounters? Pipeline,
    HostConnectionCounters Connection,
    ClientReport? Client,
    ClockOffset? Clock,
    TimeSpan? StreamingFor);

/// <summary>
/// Samples the session about once a second while it streams, classifies it with
/// <see cref="StreamHealthMonitor"/>, logs each change of state, and takes the one recovery action
/// section 8.3 allows: a keyframe, at most once per 2 s, while the client receives but shows nothing.
/// It never restarts capture, the encoder or the connection (D31, D67). <see cref="Sample"/> is the
/// whole behaviour and is what tests call; <see cref="Start"/> only runs it on a timer.
/// </summary>
public sealed class StreamDiagnostics : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ISessionControl _session;
    private readonly HostConnectionController _connection;
    private readonly IPipelineDiagnostics? _pipelineCounters;
    private readonly Action _requestKeyframe;
    private readonly Action<string>? _log;
    private readonly StreamHealthMonitor _monitor;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private long? _streamingSinceMicros;
    private StreamDiagnosticsSnapshot? _latest;

    public StreamDiagnostics(
        ISessionControl session,
        HostConnectionController connection,
        IPipelineDiagnostics? pipelineCounters,
        Action requestKeyframe,
        Action<string>? log = null,
        StreamHealthThresholds? thresholds = null)
    {
        _session = session;
        _connection = connection;
        _pipelineCounters = pipelineCounters;
        _requestKeyframe = requestKeyframe;
        _log = log;
        _monitor = new StreamHealthMonitor(thresholds);
    }

    /// <summary>Raised after every sample, on the sampling thread.</summary>
    public event EventHandler? Updated;

    public StreamDiagnosticsSnapshot? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Sample(MonotonicClock.NowMicros), null, Interval, Interval);
        }
    }

    /// <summary>Reads everything once at <paramref name="nowMicros"/> (host monotonic clock) and acts on it.</summary>
    public StreamDiagnosticsSnapshot Sample(long nowMicros)
    {
        StreamDiagnosticsSnapshot snapshot;
        bool requestKeyframe;
        lock (_gate)
        {
            SessionPhase phase = _session.State.Phase;
            PipelineDiagnosticCounters? pipeline = _pipelineCounters?.DiagnosticCounters;
            HostConnectionCounters connection = _connection.Counters;
            ClientReport? client = _connection.LatestClientReport;

            StreamHealthReport health;
            if (phase != SessionPhase.Streaming || pipeline is null)
            {
                _monitor.Reset();
                _streamingSinceMicros = null;
                health = new StreamHealthReport(StreamHealthState.NotStreaming, false, nowMicros, new StreamRates(0, 0, 0, 0, null, null), null, false);
            }
            else
            {
                _streamingSinceMicros ??= nowMicros;
                health = _monitor.Observe(new StreamSample(
                    nowMicros,
                    pipeline.TargetFrameRate,
                    pipeline.FramesCaptured,
                    pipeline.FramesSubmitted,
                    pipeline.FramesEncoded,
                    connection.Transmitted,
                    connection.DroppedQueueFull + connection.DroppedAwaitingKeyframe,
                    pipeline.SinceLastCapturedFrame,
                    client is null ? null : new ClientCounters(client.FramesReceived, client.FramesMissing, client.FramesDiscarded, client.FramesRendered, client.DecoderErrors, client.RenderLatencyMeanMs),
                    client?.ReceivedMicros ?? 0));
                if (health.Changed && health.State != StreamHealthState.NotStreaming)
                {
                    _log?.Invoke($"health {Describe(health.State)} t={nowMicros} captured_fps={health.Rates.Captured:0.0} encoded_fps={health.Rates.Encoded:0.0} sent_fps={health.Rates.Sent:0.0}");
                }
            }

            snapshot = new StreamDiagnosticsSnapshot(
                phase,
                health,
                pipeline,
                connection,
                client,
                _connection.Clock,
                _streamingSinceMicros is { } since ? TimeSpan.FromMicroseconds(nowMicros - since) : null);
            _latest = snapshot;
            requestKeyframe = health.RequestKeyframe;
        }

        if (requestKeyframe)
        {
            _log?.Invoke($"health client_stalled t={nowMicros}: forcing a keyframe (section 8.3)");
            _requestKeyframe();
        }

        Updated?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>The log and diagnostics name of a state.</summary>
    public static string Describe(StreamHealthState state) => state switch
    {
        StreamHealthState.NotStreaming => "not_streaming",
        StreamHealthState.EncoderStalled => "encoder_stalled",
        StreamHealthState.TransportStalled => "transport_stalled",
        StreamHealthState.ClientStalled => "client_stalled",
        StreamHealthState.NoClientReport => "no_client_report",
        StreamHealthState.Quiet => "quiet",
        StreamHealthState.Flowing => "flowing",
        _ => state.ToString(),
    };
}
