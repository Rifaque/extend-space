using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>Cumulative counts at one end of the measurement window, which is the time from first streaming to the stop.</summary>
internal sealed record WindowCounts(long Delivered, long Submitted, long Encoded, long Transmitted, long TransmittedBytes);

/// <summary>One second of the stream, as rates. Client rates are null until two of its reports have arrived.</summary>
internal sealed record SecondSample
{
    public double StreamSeconds { get; init; }

    public SessionPhase Phase { get; init; }

    public double CaptureFps { get; init; }

    public double EncodedFps { get; init; }

    public double TransmittedFps { get; init; }

    public double BitsPerSecond { get; init; }

    public long HostDropped { get; init; }

    public int QueueDepth { get; init; }

    public double? ReceivedFps { get; init; }

    public double? DecodedFps { get; init; }

    public double? RenderedFps { get; init; }

    public long? ClientMissing { get; init; }

    public long? ClientDiscarded { get; init; }

    public LatencySummary? RenderLatency { get; init; }

    public long? RoundTripMicros { get; init; }

    public string Describe()
    {
        static string Rate(double? value) => value is { } rate ? rate.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        string latency = RenderLatency is { } render
            ? string.Create(CultureInfo.InvariantCulture, $"mean {render.MeanMs:0.0} p95 {render.P95Ms:0.0} max {render.MaxMs:0.0} ms")
            : "-";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{StreamSeconds,6:0.0} s] {Phase,-12} host: capture {CaptureFps,5:0.0} encoded {EncodedFps,5:0.0} sent {TransmittedFps,5:0.0} fps {BitsPerSecond / 1e6,6:0.00} Mbit/s queue {QueueDepth} dropped {HostDropped}"
            + $" | client: received {Rate(ReceivedFps)} decoded {Rate(DecodedFps)} rendered {Rate(RenderedFps)} fps missing {ClientMissing?.ToString(CultureInfo.InvariantCulture) ?? "-"}"
            + $" | capture-to-render {latency}");
    }
}

/// <summary>Everything one streaming run found out. As in the file report, what was not measured stays null and prints as unavailable.</summary>
internal sealed class StreamReport(CaptureOptions options, StreamOptions stream)
{
    private const string Unavailable = "unavailable";

    public CaptureOptions Options { get; } = options;

    public StreamOptions Stream { get; } = stream;

    /// <summary>The capture source and encoder, described exactly as the file report describes them.</summary>
    public RunReport Pipeline { get; } = new(options);

    public List<string> Errors { get; } = [];

    /// <summary>Connection and state changes, in order, with the time since the run started.</summary>
    public List<string> Events { get; } = [];

    public List<SecondSample> Seconds { get; } = [];

    public string? StopReason { get; set; }

    public SessionPhase FinalPhase { get; set; }

    public string? Transport { get; set; }

    public StreamConfig? Config { get; set; }

    public Hello? Client { get; set; }

    public ClientCapabilities? Capabilities { get; set; }

    public StreamReady? Ready { get; set; }

    public FrameCounters Frames { get; set; } = new();

    public long SkippedNoClient { get; set; }

    public HostStreamCounters Host { get; set; } = new();

    public ClientStats ClientTotals { get; set; } = new();

    public TimeSpan? WaitedForClient { get; set; }

    public long? StreamWindowStart { get; set; }

    public long? StreamWindowEnd { get; set; }

    /// <summary>Time actually spent in the Streaming state, which is less than the window if the connection was lost.</summary>
    public TimeSpan TimeStreaming { get; set; }

    public WindowCounts? WindowAtStart { get; set; }

    public WindowCounts? WindowAtEnd { get; set; }

    public ClockOffset? Clock { get; set; }

    public (LatencyAggregate Receive, LatencyAggregate Render, LatencyAggregate Display)? Latency { get; set; }

    public DurationSummary? HostDelay { get; set; }

    public DurationSummary? DeliveryDelay { get; set; }

    public DurationSummary? EncoderTurnaround { get; set; }

    public long KeyframeRequestsAccepted { get; set; }

    public long KeyframeRequestsRefused { get; set; }

    public TimeSpan? ProcessorTime { get; set; }

    public TimeSpan? ProcessorTimeWindow { get; set; }

    public long? PeakWorkingSetBytes { get; set; }

    /// <summary>A stream succeeded only if nothing failed and the client reports having rendered frames.</summary>
    public bool Succeeded => Errors.Count == 0 && Host.Transmitted > 0 && ClientTotals.FramesRendered > 0;

    public TimeSpan? StreamWindow =>
        StreamWindowStart is { } start && StreamWindowEnd is { } end ? Stopwatch.GetElapsedTime(start, end) : null;

    public void Write(TextWriter writer)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        void Line(FormattableString text) => writer.WriteLine(text.ToString(invariant));
        void Heading(string text)
        {
            writer.WriteLine();
            writer.WriteLine(text);
        }

        writer.WriteLine();
        writer.WriteLine("==== ExtendSpace live stream over adb forward: run report ====");
        Line($"Result: {(Succeeded ? "SUCCEEDED" : "FAILED")}");
        Line($"Stopped because: {StopReason ?? "the run did not get as far as streaming"}");
        Line($"Final session state: {FinalPhase}");

        Pipeline.WriteSourceAndEncoder(writer);

        Heading("Connection");
        Line($"  Transport:          {Transport ?? Unavailable}");
        Line($"  Client:             {(Client is null ? "none completed the handshake" : $"'{Client.DeviceName}', app {Client.AppVersion}, protocol {Client.ProtocolMin}-{Client.ProtocolMax}")}");
        if (Capabilities is { } capabilities)
        {
            Line($"  Client display:     {capabilities.DisplayWidth} x {capabilities.DisplayHeight} at {capabilities.RefreshHz:0.#} Hz [reported by the client]");
            foreach (DecoderCapability decoder in capabilities.Decoders)
            {
                Line($"  Client decoder:     {decoder.Name}, {(decoder.Hardware ? "hardware" : "software")}, up to {decoder.MaxWidth} x {decoder.MaxHeight}{(decoder.LowLatency ? ", low-latency feature" : string.Empty)} [reported]");
            }
        }

        Line($"  Decoder in use:     {Ready?.Decoder ?? Unavailable}{(Ready?.Hardware is { } hardware ? (hardware ? " (hardware)" : " (software)") : string.Empty)} [as the client announced]");
        if (Config is { } config)
        {
            Line($"  Stream sent:        {config.Codec}, {config.Profile} profile, {config.Width} x {config.Height}, {config.FrameRate} fps and {config.Bitrate:N0} bit/s requested");
        }

        Line($"  Connections:        {Host.ConnectionsEstablished} established, {Host.ConnectionsLost} lost");
        Line($"  Waited for client:  {Elapsed(WaitedForClient)}");
        Line($"  Measurement window: {Elapsed(StreamWindow)} from the first handshake to the stop, of which {TimeStreaming.TotalSeconds:0.000} s in the Streaming state");

        Heading("Events");
        foreach (string entry in Events)
        {
            Line($"  {entry}");
        }

        Heading("Frames on the host, whole run [measured]");
        Line($"  Delivered by the capture API:             {Frames.Delivered}");
        Line($"  Skipped by the pacer (arrived early):     {Frames.SkippedByPacer}");
        Line($"  Skipped, no client streaming (unencoded): {SkippedNoClient}");
        Line($"  Dropped, encoder not ready for input:     {Frames.DroppedEncoderBusy}");
        Line($"  Dropped, no free encoder input surface:   {Frames.DroppedNoInputSurface}");
        Line($"  Discarded when the run stopped:           {Frames.DiscardedAtStop}");
        Line($"  Submitted to the encoder:                 {Frames.Submitted}");
        Line($"  Encoded (access units from the encoder):  {Host.Encoded}");
        Line($"    not sent, no client streaming:          {Host.NotSentNoClient}");
        Line($"    dropped, waiting for a keyframe:        {Host.DroppedAwaitingKeyframe}");
        Line($"    dropped, send queue full:               {Host.DroppedQueueFull}");
        Line($"    dropped, larger than a frame may be:    {Host.DroppedTooLarge}");
        Line($"    discarded, still queued at disconnect:  {Host.DiscardedAtDisconnect}");
        Line($"    transmitted (written to the socket):    {Host.Transmitted}, of which {Host.KeyframesTransmitted} keyframes, {Host.TransmittedBytes:N0} bytes");
        if (Host.Unaccounted != 0)
        {
            Line($"    UNACCOUNTED FOR:                        {Host.Unaccounted}");
        }

        Line($"  Keyframes asked of the encoder:           {Host.KeyframeRequests} ({KeyframeRequestsAccepted} accepted by it, {KeyframeRequestsRefused} refused)");

        Heading("Frames on the client, all connections [reported by the client]");
        Line($"  Received:                                 {ClientTotals.FramesReceived}, {ClientTotals.BytesReceived:N0} bytes");
        Line($"  Missing (gaps in sequence numbers):       {ClientTotals.FramesMissing}");
        Line($"  Discarded before the decoder:             {ClientTotals.FramesDiscarded}");
        Line($"  Submitted to the decoder:                 {ClientTotals.FramesSubmitted}");
        Line($"  Decoded:                                  {ClientTotals.FramesDecoded}");
        Line($"  Rendered (released to the surface):       {ClientTotals.FramesRendered}");
        Line($"  Displayed (system callback, informational): {ClientTotals.FramesDisplayed}");
        Line($"  Decoder errors:                           {ClientTotals.DecoderErrors}");
        Line($"  Transmitted by the host but not reported received: {Host.Transmitted - ClientTotals.FramesReceived}");

        Heading("Rates over the measurement window [measured counts / window length]");
        if (WindowAtStart is { } first && WindowAtEnd is { } last && StreamWindow is { } window && window > TimeSpan.Zero)
        {
            double seconds = window.TotalSeconds;
            Line($"  Captured:           {(last.Delivered - first.Delivered) / seconds:0.00} fps");
            Line($"  Encoded:            {(last.Encoded - first.Encoded) / seconds:0.00} fps");
            Line($"  Transmitted:        {(last.Transmitted - first.Transmitted) / seconds:0.00} fps, {(last.TransmittedBytes - first.TransmittedBytes) * 8 / seconds / 1e6:0.000} Mbit/s ({(last.TransmittedBytes - first.TransmittedBytes) / seconds:N0} bytes/s)");
            Line($"  Received (client):  {ClientTotals.FramesReceived / seconds:0.00} fps");
            Line($"  Decoded (client):   {ClientTotals.FramesDecoded / seconds:0.00} fps");
            Line($"  Rendered (client):  {ClientTotals.FramesRendered / seconds:0.00} fps");
        }
        else
        {
            Line($"  {Unavailable}: the stream never started");
        }

        List<SecondSample> streamed = [.. Seconds.Where(sample => sample.Phase == SessionPhase.Streaming)];
        if (streamed.Count > 0)
        {
            Line($"  Per second while streaming (n = {streamed.Count}), minimum / median / maximum:");
            Line($"    captured fps:     {Spread(streamed.Select(sample => (double?)sample.CaptureFps))}");
            Line($"    transmitted fps:  {Spread(streamed.Select(sample => (double?)sample.TransmittedFps))}");
            Line($"    rendered fps:     {Spread(streamed.Select(sample => sample.RenderedFps))}");
            Line($"    Mbit/s:           {Spread(streamed.Select(sample => (double?)(sample.BitsPerSecond / 1e6)))}");
        }

        Heading("Latency [measured on two clocks related by ping/pong; see the note]");
        if (Clock is { } clock)
        {
            Line($"  Clock relation:     best round trip {clock.RoundTripMicros / 1000.0:0.000} ms, so every figure below is good to +/- {clock.RoundTripMicros / 2000.0:0.000} ms");
        }
        else
        {
            Line($"  Clock relation:     {Unavailable}; no pong was received, so the client could not report latency");
        }

        Line($"  Capture -> encoded on the host:               {Describe(HostDelay)}");
        Line($"    (frame handed over by Windows to the encoded picture reaching the sender; one clock, no estimate)");
        Line($"  Encoder turnaround alone:                     {Describe(EncoderTurnaround)}");
        if (Latency is { } latency)
        {
            Line($"  Capture -> received by the app:               {Describe(latency.Receive)}");
            Line($"  Capture -> decoded and released to the surface: {Describe(latency.Render)}");
            if (latency.Display.Frames > 0)
            {
                Line($"  Capture -> shown, per the system's callback:  {Describe(latency.Display)}");
            }
            else
            {
                Line($"  Capture -> actually shown on the tablet:      {Unavailable}. Android's frame-rendered callback returned the");
                Line($"    timestamp the frame was queued with, not a display time, so the client does not report it.");
            }
        }

        Line($"  Windows' own frame timestamp, relative to the hand-over: {Describe(DeliveryDelay)}");
        Line($"    (hand-over time minus the stamp; negative means Windows stamped the frame ahead of delivering it)");
        Line($"  Note: 'capture' is the moment Windows handed the frame to this process. Not included: the time");
        Line($"  before that (application drawing, composition), display scan-out and pixel response on either device.");
        Line($"  This is not a glass-to-glass figure; that needs a camera looking at both screens.");

        Heading("Resources [measured, host process only]");
        Line($"  Processor time:     {(ProcessorTime is { } used && ProcessorTimeWindow is { } over && Metrics.CpuPercentOfOneCore(used, over) is { } percent ? string.Create(invariant, $"{used.TotalSeconds:0.000} s over {over.TotalSeconds:0.000} s = {percent:0.0}% of one logical processor") : Unavailable)}");
        Line($"  Peak working set:   {(PeakWorkingSetBytes is { } peak ? string.Create(invariant, $"{Metrics.ToMebibytes(peak):0.0} MiB") : Unavailable)}");

        Heading("Errors");
        if (Errors.Count == 0)
        {
            Line($"  none");
        }

        foreach (string error in Errors)
        {
            Line($"  {error}");
        }

        writer.WriteLine();
    }

    private static string Elapsed(TimeSpan? value) =>
        value is { } time ? string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.000} s") : Unavailable;

    private static string Describe(DurationSummary? summary) => summary is { } s
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"mean {s.Mean.TotalMilliseconds:0.00} ms, median {s.Median.TotalMilliseconds:0.00}, p95 {s.Percentile95.TotalMilliseconds:0.00}, min {s.Minimum.TotalMilliseconds:0.00}, max {s.Maximum.TotalMilliseconds:0.00} (n = {s.Count})")
        : Unavailable;

    private static string Describe(LatencyAggregate aggregate) => aggregate.Frames > 0
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"mean {aggregate.MeanMs:0.00} ms, min {aggregate.MinMs:0.00}, max {aggregate.MaxMs:0.00}; per-second p95: median {aggregate.MedianP95Ms:0.00}, worst {aggregate.WorstP95Ms:0.00} (n = {aggregate.Frames} frames in {aggregate.Reports} reports)")
        : Unavailable;

    private static string Spread(IEnumerable<double?> values)
    {
        double[] known = [.. values.Where(value => value.HasValue).Select(value => value!.Value).Order()];
        return known.Length == 0
            ? Unavailable
            : string.Create(CultureInfo.InvariantCulture, $"{known[0]:0.00} / {known[(known.Length - 1) / 2]:0.00} / {known[^1]:0.00}");
    }
}
