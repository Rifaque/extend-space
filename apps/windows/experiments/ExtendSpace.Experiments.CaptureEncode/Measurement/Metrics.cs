namespace ExtendSpace.Experiments.CaptureEncode.Measurement;

/// <summary>Derived figures. Each returns null rather than a made-up value when it cannot be computed.</summary>
internal static class Metrics
{
    public const double BytesPerMebibyte = 1024 * 1024;

    public static double ToMebibytes(long bytes) => bytes / BytesPerMebibyte;

    public static double? FramesPerSecond(long frames, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? frames / elapsed.TotalSeconds : null;

    public static double? BitsPerSecond(long bytes, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? bytes * 8 / elapsed.TotalSeconds : null;

    /// <summary>Processor time as a percentage of one logical processor over the elapsed time.</summary>
    public static double? CpuPercentOfOneCore(TimeSpan processorTime, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? processorTime / elapsed * 100 : null;
}
