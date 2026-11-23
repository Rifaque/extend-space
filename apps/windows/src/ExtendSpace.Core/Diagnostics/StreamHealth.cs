namespace ExtendSpace.Core.Diagnostics;

/// <summary>
/// What one second of the stream looked like, as layer 1 of the stall design classifies it
/// (architecture section 8.2, D67). Ordered from worst to best where a second shows several.
/// </summary>
public enum StreamHealthState
{
    /// <summary>No stream: not streaming, or not yet sampled twice.</summary>
    NotStreaming,

    /// <summary>Frames were submitted to the encoder and none came out for the encoder threshold.</summary>
    EncoderStalled,

    /// <summary>The send queue dropped frames in each of several consecutive seconds: the link is not draining.</summary>
    TransportStalled,

    /// <summary>The client keeps receiving frames but shows none of them.</summary>
    ClientStalled,

    /// <summary>The client has sent no statistics for longer than it should.</summary>
    NoClientReport,

    /// <summary>
    /// Windows is delivering fewer pictures than the stream's rate, and every later stage keeps up.
    /// This is what a still screen looks like. It is <b>not</b> reported as a fault: starvation and a
    /// still screen cannot be told apart from these counters (section 8.1; layer 2 is not built).
    /// </summary>
    Quiet,

    /// <summary>Pictures are arriving at about the stream's rate and every stage keeps up.</summary>
    Flowing,
}

/// <summary>Cumulative counters read at one moment, all on the host's monotonic clock.</summary>
/// <param name="NowMicros">When the counters were read.</param>
/// <param name="TargetFrameRate">The stream's negotiated frame rate.</param>
/// <param name="FramesCaptured">Pictures Windows delivered and the pacer kept.</param>
/// <param name="FramesSubmitted">Pictures given to the encoder, including repeats for a still screen.</param>
/// <param name="FramesEncoded">Access units the encoder handed over.</param>
/// <param name="FramesSent">Video frames fully written to the socket.</param>
/// <param name="FramesDroppedAtQueue">Frames the send queue dropped (overflow and awaiting a keyframe).</param>
/// <param name="SinceLastCapturedFrame">Time since Windows last delivered a picture; null before the first.</param>
/// <param name="Client">The latest client report; null before the first.</param>
/// <param name="ClientReportMicros">When that report was received.</param>
public sealed record StreamSample(
    long NowMicros,
    int TargetFrameRate,
    long FramesCaptured,
    long FramesSubmitted,
    long FramesEncoded,
    long FramesSent,
    long FramesDroppedAtQueue,
    TimeSpan? SinceLastCapturedFrame,
    ClientCounters? Client,
    long ClientReportMicros);

/// <summary>The client's cumulative counters from its last <c>stats</c> message.</summary>
public sealed record ClientCounters(long Received, long Missing, long Discarded, long Rendered, long DecoderErrors, double? RenderLatencyMeanMs);

/// <summary>Frames per second over the last interval, each measured where it happens.</summary>
public sealed record StreamRates(double Captured, double Encoded, double Sent, double DroppedAtQueue, double? ClientReceived, double? ClientRendered);

/// <param name="Changed">The state differs from the previous report's.</param>
/// <param name="StateSinceMicros">When the current state began.</param>
/// <param name="RequestKeyframe">The client is stalled and a keyframe should be forced now (at most once per <see cref="StreamHealthThresholds.ClientStallKeyframeInterval"/>).</param>
public sealed record StreamHealthReport(
    StreamHealthState State,
    bool Changed,
    long StateSinceMicros,
    StreamRates Rates,
    TimeSpan? SinceLastCapturedFrame,
    bool RequestKeyframe);

/// <summary>
/// The thresholds of section 8.2. Initial values, each with its reason there; to be validated on
/// devices (D8 stays open for the values, not the mechanism).
/// </summary>
public sealed record StreamHealthThresholds
{
    /// <summary>A frame submitted and nothing out for this long. Twenty times the worst turnaround measured (23.9 ms).</summary>
    public TimeSpan EncoderStall { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Queue drops in this many consecutive samples. One drop-and-recover is the mechanism working.</summary>
    public int TransportStallSamples { get; init; } = 3;

    /// <summary>Client receiving but rendering nothing for this long; its own recovery acts within 500 ms.</summary>
    public TimeSpan ClientStall { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>At most one forced keyframe per this interval while the client stays stalled (section 8.3).</summary>
    public TimeSpan ClientStallKeyframeInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>No client report for this long. The client reports once a second.</summary>
    public TimeSpan ClientReportMissing { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>Capture below this share of the stream's rate is quiet.</summary>
    public double QuietBelowShareOfTarget { get; init; } = 0.5;
}

/// <summary>
/// Layer 1 of the stall design (architecture section 8.2, D67): compares neighbouring counters of one
/// stream, sample by sample, and says where the picture is stopping. Pure and single-threaded: the
/// caller samples (about once a second) and acts on <see cref="StreamHealthReport.RequestKeyframe"/>.
/// It never restarts anything; the only action it asks for is the one section 8.3 allows.
/// </summary>
public sealed class StreamHealthMonitor(StreamHealthThresholds? thresholds = null)
{
    private readonly StreamHealthThresholds _thresholds = thresholds ?? new StreamHealthThresholds();
    private StreamSample? _previous;
    private StreamHealthState _state = StreamHealthState.NotStreaming;
    private long _stateSince;

    private long _lastEncodedChangeMicros;
    private long _submittedAtLastEncodedChange;
    private int _droppingSamples;
    private long? _clientStallSinceMicros;
    private long? _lastForcedMicros;
    private long _streamStartMicros;
    private double? _clientReceivedRate;
    private double? _clientRenderedRate;

    public StreamHealthState State => _state;

    /// <summary>Forget the stream: the next sample starts a new one.</summary>
    public void Reset()
    {
        _previous = null;
        _state = StreamHealthState.NotStreaming;
        _droppingSamples = 0;
        _clientStallSinceMicros = null;
        _lastForcedMicros = null;
    }

    public StreamHealthReport Observe(StreamSample sample)
    {
        StreamSample? previous = _previous;
        _previous = sample;
        if (previous is null || sample.NowMicros <= previous.NowMicros)
        {
            _lastEncodedChangeMicros = sample.NowMicros;
            _submittedAtLastEncodedChange = sample.FramesSubmitted;
            _streamStartMicros = sample.NowMicros;
            _clientReceivedRate = null;
            _clientRenderedRate = null;
            return Report(StreamHealthState.NotStreaming, sample, Rates(sample, sample), false);
        }

        StreamRates rates = Rates(previous, sample);

        // Client rates change only when a new report arrives; between reports the last ones stand.
        if (rates.ClientReceived is not null)
        {
            _clientReceivedRate = rates.ClientReceived;
            _clientRenderedRate = rates.ClientRendered;
        }
        else
        {
            rates = rates with { ClientReceived = _clientReceivedRate, ClientRendered = _clientRenderedRate };
        }

        // Encoder: something went in after the last output, and nothing has come out since for the threshold.
        if (sample.FramesEncoded != previous.FramesEncoded)
        {
            _lastEncodedChangeMicros = sample.NowMicros;
            _submittedAtLastEncodedChange = sample.FramesSubmitted;
        }

        bool encoderStalled = sample.FramesSubmitted > _submittedAtLastEncodedChange
            && sample.NowMicros - _lastEncodedChangeMicros >= Micros(_thresholds.EncoderStall);

        // Transport: drops in every one of the last N samples.
        _droppingSamples = sample.FramesDroppedAtQueue > previous.FramesDroppedAtQueue ? _droppingSamples + 1 : 0;
        bool transportStalled = _droppingSamples >= _thresholds.TransportStallSamples;

        // Client: from its own reports only; a report that has not changed says nothing new.
        long reportBase = sample.Client is null ? _streamStartMicros : Math.Max(sample.ClientReportMicros, _streamStartMicros);
        bool reportMissing = sample.NowMicros - reportBase >= Micros(_thresholds.ClientReportMissing);
        if (sample.Client is { } client && previous.Client is { } before && sample.ClientReportMicros != previous.ClientReportMicros)
        {
            if (client.Rendered > before.Rendered || client.Received == before.Received)
            {
                _clientStallSinceMicros = null;
            }
            else
            {
                _clientStallSinceMicros ??= previous.ClientReportMicros;
            }
        }

        bool clientStalled = _clientStallSinceMicros is { } since && sample.NowMicros - since >= Micros(_thresholds.ClientStall);
        if (!clientStalled)
        {
            _lastForcedMicros = null;
        }

        bool requestKeyframe = clientStalled
            && (_lastForcedMicros is not { } forced || sample.NowMicros - forced >= Micros(_thresholds.ClientStallKeyframeInterval));
        if (requestKeyframe)
        {
            _lastForcedMicros = sample.NowMicros;
        }

        StreamHealthState state = encoderStalled ? StreamHealthState.EncoderStalled
            : transportStalled ? StreamHealthState.TransportStalled
            : clientStalled ? StreamHealthState.ClientStalled
            : reportMissing ? StreamHealthState.NoClientReport
            : rates.Captured < sample.TargetFrameRate * _thresholds.QuietBelowShareOfTarget ? StreamHealthState.Quiet
            : StreamHealthState.Flowing;

        return Report(state, sample, rates, requestKeyframe);
    }

    private StreamHealthReport Report(StreamHealthState state, StreamSample sample, StreamRates rates, bool requestKeyframe)
    {
        bool changed = state != _state;
        if (changed)
        {
            _state = state;
            _stateSince = sample.NowMicros;
        }

        return new StreamHealthReport(state, changed, _stateSince, rates, sample.SinceLastCapturedFrame, requestKeyframe);
    }

    private static StreamRates Rates(StreamSample from, StreamSample to)
    {
        double seconds = (to.NowMicros - from.NowMicros) / 1_000_000.0;
        if (seconds <= 0)
        {
            return new StreamRates(0, 0, 0, 0, null, null);
        }

        double Per(long a, long b) => Math.Max(0, b - a) / seconds;
        double? clientSeconds = to.Client is not null && from.Client is not null && to.ClientReportMicros > from.ClientReportMicros
            ? (to.ClientReportMicros - from.ClientReportMicros) / 1_000_000.0
            : null;
        return new StreamRates(
            Per(from.FramesCaptured, to.FramesCaptured),
            Per(from.FramesEncoded, to.FramesEncoded),
            Per(from.FramesSent, to.FramesSent),
            Per(from.FramesDroppedAtQueue, to.FramesDroppedAtQueue),
            clientSeconds is { } cs ? Math.Max(0, to.Client!.Received - from.Client!.Received) / cs : null,
            clientSeconds is { } cs2 ? Math.Max(0, to.Client!.Rendered - from.Client!.Rendered) / cs2 : null);
    }

    private static long Micros(TimeSpan span) => (long)span.TotalMicroseconds;
}
