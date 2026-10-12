using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Bitstream;
using ExtendSpace.Experiments.CaptureEncode.Encoding;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode;

/// <summary>
/// Everything one run found out. Fields stay null until the run actually got that far, and the
/// printed report says "unavailable" for them instead of showing a number that was never measured.
/// </summary>
internal sealed class RunReport(CaptureOptions options)
{
    public CaptureOptions Options { get; } = options;

    public List<string> Errors { get; } = [];

    public List<string> RejectedEncoders { get; } = [];

    public IReadOnlyList<string> EncoderSettings { get; set; } = [];

    public string? StopReason { get; set; }

    public DisplayMonitor? Monitor { get; set; }

    public (int Width, int Height)? CaptureSize { get; set; }

    public OutputGeometry? Geometry { get; set; }

    public string? AdapterName { get; set; }

    public string? FeatureLevel { get; set; }

    public string? Border { get; set; }

    public string? RateLimit { get; set; }

    public string? TimerResolution { get; set; }

    public EncoderEvidence? Encoder { get; set; }

    public AccelerationVerdict? Acceleration { get; set; }

    public FrameCounters Frames { get; set; } = new();

    /// <summary>From starting the capture session to stopping it.</summary>
    public TimeSpan? CaptureElapsed { get; set; }

    /// <summary>From starting the capture session to writing the last encoded sample.</summary>
    public TimeSpan? ElapsedToLastOutput { get; set; }

    public TimeSpan? DrainElapsed { get; set; }

    public TimeSpan? CapturedTimeSpan { get; set; }

    public DurationSummary? DeliveryIntervals { get; set; }

    public DurationSummary? ConversionCalls { get; set; }

    public DurationSummary? EncoderTurnaround { get; set; }

    public long UnmatchedOutputs { get; set; }

    public string? OutputPath { get; set; }

    public long? OutputBytes { get; set; }

    public H264StreamStatistics? Stream { get; set; }

    public bool SequenceHeaderPrepended { get; set; }

    public TimeSpan? ProcessorTime { get; set; }

    public TimeSpan? ProcessorTimeWindow { get; set; }

    public long? PeakWorkingSetBytes { get; set; }

    /// <summary>A run succeeded only if nothing failed and encoded video really reached the file.</summary>
    public bool Succeeded => Errors.Count == 0 && Frames.Encoded > 0 && OutputBytes > 0;

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
        writer.WriteLine("==== ExtendSpace capture -> H.264 experiment: run report ====");
        Line($"Result: {(Succeeded ? "SUCCEEDED" : "FAILED")}");
        Line($"Stopped because: {StopReason ?? "the run did not get as far as capturing"}");

        Heading("Capture source [measured]");
        Line($"  Monitor:            {Describe(Monitor)}");
        Line($"  Capture API:        Windows.Graphics.Capture, free-threaded frame pool, BGRA8, {Capture.MonitorCapture.FramePoolBuffers} buffers");
        Line($"  Capture resolution: {Describe(CaptureSize)} (reported by the capture item)");
        Line($"  Encoded resolution: {Describe(Geometry)}");
        Line($"  Graphics adapter:   {AdapterName ?? Unavailable} (Direct3D {FeatureLevel ?? Unavailable})");
        Line($"  Cursor in capture:  {(Options.CaptureCursor ? "yes" : "no")}");
        Line($"  Capture border:     {Border ?? Unavailable}");
        Line($"  Frame-rate limit:   {RateLimit ?? Unavailable}");
        Line($"  Timer resolution:   {TimerResolution ?? Unavailable}");

        Heading("Configuration [requested, not measured]");
        Line($"  Duration:           {Options.Duration.TotalSeconds:0.###} s{(Options.MaxFrames is { } max ? $", or {max} frames if sooner" : string.Empty)}");
        Line($"  Frame rate:         {Options.FrameRate} fps");
        Line($"  Bitrate:            {Options.BitrateBitsPerSecond:N0} bit/s");
        Line($"  Keyframe interval:  {Options.KeyframeIntervalFrames} frames ({Options.KeyframeIntervalSeconds:0.###} s)");
        Line($"  Output size:        {(Options.OutputSize is { } size ? $"{size.Width} x {size.Height}" : "the monitor's own size")}");
        Line($"  H.264 profile:      {Options.Profile}");
        Line($"  Encoder preference: {Options.Encoder}");

        Heading("Encoder [identity as reported by Media Foundation]");
        if (Encoder is null)
        {
            Line($"  No encoder was opened.");
        }
        else
        {
            Line($"  Name:               {Encoder.Name}");
            Line($"  CLSID:              {Encoder.Clsid:B}");
            Line($"  Acceleration:       {Acceleration?.Status.ToString().ToUpperInvariant() ?? Unavailable}");
            Line($"    Why:              {Acceleration?.Reason ?? Unavailable}");
            Line($"    Evidence:         listed as hardware transform: {YesNo(Encoder.EnumeratedAsHardware)}; hardware vendor: {Encoder.HardwareVendorId ?? "none"}");
            Line($"                      hardware device URL: {Encoder.HardwareUrl ?? "none"}");
            Line($"                      asynchronous: {YesNo(Encoder.IsAsync)}; Direct3D 11 aware: {YesNo(Encoder.IsD3D11Aware)}; accepted the device manager: {YesNo(Encoder.AcceptedD3DManager)}");
            Line($"  Encoder properties (ICodecAPI):");
            foreach (string setting in EncoderSettings)
            {
                Line($"    {setting}");
            }
        }

        foreach (string rejected in RejectedEncoders)
        {
            Line($"  Tried and rejected: {rejected}");
        }

        Heading("Frames [measured]");
        Line($"  Delivered by the capture API:           {Frames.Delivered}");
        Line($"  Skipped by the pacer (arrived early):   {Frames.SkippedByPacer}");
        Line($"  Dropped, encoder not ready for input:   {Frames.DroppedEncoderBusy}");
        Line($"  Dropped, no free encoder input surface: {Frames.DroppedNoInputSurface}");
        Line($"  Discarded when the run stopped:         {Frames.DiscardedAtStop}");
        Line($"  Submitted to the encoder:               {Frames.Submitted}");
        Line($"  Encoded and written to the file:        {Frames.Encoded}");
        if (Frames.Unaccounted != 0)
        {
            Line($"  UNACCOUNTED FOR:                        {Frames.Unaccounted} (a frame was lost to an error part-way through)");
        }

        if (Frames.LostInEncoder != 0)
        {
            Line($"  Submitted but never returned:           {Frames.LostInEncoder}");
        }

        Line($"  Not countable: frames Windows composed but did not deliver (static screen, or its own rate limit).");

        Heading("Timing [measured]");
        Line($"  Capture duration (wall clock):    {Seconds(CaptureElapsed)} (requested {Options.Duration.TotalSeconds:0.###} s)");
        Line($"  First to last delivered frame:    {Seconds(CapturedTimeSpan)} (capture API timestamps)");
        Line($"  Average capture rate:             {Rate(Frames.Delivered, CaptureElapsed)} (delivered frames / capture duration)");
        Line($"  Average submission rate:          {Rate(Frames.Submitted, CaptureElapsed)} (submitted frames / capture duration)");
        Line($"  Average encoded output rate:      {Rate(Frames.Encoded, ElapsedToLastOutput)} (encoded frames / time to the last encoded frame)");
        Line($"  Interval between delivered frames: {Describe(DeliveryIntervals)}");
        Line($"  BGRA->NV12 conversion call:       {Describe(ConversionCalls)}");
        Line($"    (CPU-side cost of queueing the GPU conversion; the GPU's own time is not measured)");
        Line($"  Encoder turnaround:               {Describe(EncoderTurnaround)}");
        Line($"    (frame handed to the encoder -> its encoded sample received by this process)");
        if (UnmatchedOutputs != 0)
        {
            Line($"    {UnmatchedOutputs} output sample(s) carried a timestamp matching no input, so are missing from the turnaround figures");
        }

        Line($"  Drain after the last frame:       {Seconds(DrainElapsed)}");

        Heading("Output [measured]");
        Line($"  File:               {OutputPath ?? "none was produced"}");
        Line($"  Size:               {(OutputBytes is { } bytes ? string.Create(invariant, $"{bytes:N0} bytes ({Metrics.ToMebibytes(bytes):0.000} MiB)") : Unavailable)}");
        Line($"  Average bitrate:    {Bitrate()} (file size / capture duration; target {Options.BitrateBitsPerSecond / 1e6:0.###} Mbit/s)");
        if (Stream is { } stream)
        {
            Line($"  Stream structure, scanned as it was written (framing only, nothing is decoded):");
            Line($"    Access units: {stream.AccessUnits} = {stream.IdrAccessUnits} IDR + {stream.NonIdrAccessUnits} non-IDR + {stream.MalformedAccessUnits} malformed");
            Line($"    NAL units: {stream.SpsNalUnits} SPS, {stream.PpsNalUnits} PPS, {stream.SliceNalUnits} slices, {stream.SeiNalUnits} SEI, {stream.AccessUnitDelimiters} access unit delimiters");
            Line($"    IDR access units that carry SPS and PPS ahead of the picture: {stream.IdrAccessUnitsWithParameterSets} of {stream.IdrAccessUnits}");
            Line($"    Starts with an IDR picture and its parameter sets: {(stream.StartsWithDecodableKeyframe is { } starts ? YesNo(starts) : Unavailable)}");
            Line($"    SPS and PPS written from the encoder's output type instead: {YesNo(SequenceHeaderPrepended)}");
            Line($"    Profile and level in the SPS: {DescribeProfile(stream)}");
        }

        Heading("Resources [measured, this process only]");
        Line($"  Processor time:     {ProcessorUse()}");
        Line($"  Peak working set:   {(PeakWorkingSetBytes is { } peak ? string.Create(invariant, $"{Metrics.ToMebibytes(peak):0.0} MiB") : Unavailable)}");
        Line($"  Not measured:       GPU load, the encoder's work outside this process, and the Desktop Window Manager's share of the capture.");
        Line($"  Not measurable here: end-to-end or glass-to-glass latency. There is no transport, decoder or second display in this experiment.");

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

    private const string Unavailable = "unavailable";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Describe(DisplayMonitor? monitor) => monitor is null
        ? Unavailable
        : $"{monitor.DeviceName}, index {monitor.Index}{(monitor.IsPrimary ? ", primary" : string.Empty)}, at ({monitor.Left}, {monitor.Top})";

    private static string Describe(OutputGeometry? geometry)
    {
        if (geometry is null)
        {
            return Unavailable;
        }

        string size = $"{geometry.OutputWidth} x {geometry.OutputHeight}";
        if (!geometry.IsScaled)
        {
            return $"{size} (not scaled)";
        }

        string bars = geometry.HasBars
            ? $"; the picture is {geometry.PictureWidth} x {geometry.PictureHeight} at ({geometry.PictureX}, {geometry.PictureY}), the rest black"
            : string.Empty;
        return $"{size} (scaled from {geometry.SourceWidth} x {geometry.SourceHeight} by the GPU video processor{bars})";
    }

    private static string Describe((int Width, int Height)? size) =>
        size is { } value ? $"{value.Width} x {value.Height}" : Unavailable;

    private static string Describe(DurationSummary? summary) => summary is { } s
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"mean {s.Mean.TotalMilliseconds:0.00} ms, median {s.Median.TotalMilliseconds:0.00}, p95 {s.Percentile95.TotalMilliseconds:0.00}, min {s.Minimum.TotalMilliseconds:0.00}, max {s.Maximum.TotalMilliseconds:0.00} (n = {s.Count})")
        : Unavailable;

    private static string Seconds(TimeSpan? value) =>
        value is { } time ? string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.000} s") : Unavailable;

    private static string Rate(long frames, TimeSpan? elapsed) =>
        elapsed is { } time && Metrics.FramesPerSecond(frames, time) is { } rate
            ? string.Create(CultureInfo.InvariantCulture, $"{rate:0.00} fps")
            : Unavailable;

    private string Bitrate() =>
        OutputBytes is { } bytes && CaptureElapsed is { } elapsed && Metrics.BitsPerSecond(bytes, elapsed) is { } rate
            ? string.Create(CultureInfo.InvariantCulture, $"{rate / 1e6:0.000} Mbit/s")
            : Unavailable;

    private string ProcessorUse()
    {
        if (ProcessorTime is not { } used
            || ProcessorTimeWindow is not { } window
            || Metrics.CpuPercentOfOneCore(used, window) is not { } percentOfOne)
        {
            return Unavailable;
        }

        int processors = Environment.ProcessorCount;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{used.TotalSeconds:0.000} s over {window.TotalSeconds:0.000} s = {percentOfOne:0.0}% of one logical processor ({percentOfOne / processors:0.00}% of all {processors})");
    }

    private static string DescribeProfile(H264StreamStatistics stream)
    {
        if (stream.ProfileIdc is not { } profile || stream.LevelIdc is not { } level)
        {
            return "no SPS was seen";
        }

        string name = profile switch
        {
            66 => "Baseline",
            77 => "Main",
            88 => "Extended",
            100 => "High",
            _ => "other",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"profile_idc {profile} ({name}), constraint flags 0x{stream.ConstraintFlags:X2}, level_idc {level} (level {level / 10.0:0.0})");
    }
}
