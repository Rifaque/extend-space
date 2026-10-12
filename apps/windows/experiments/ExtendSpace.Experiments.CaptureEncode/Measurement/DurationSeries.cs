namespace ExtendSpace.Experiments.CaptureEncode.Measurement;

internal readonly record struct DurationSummary(
    int Count,
    TimeSpan Mean,
    TimeSpan Minimum,
    TimeSpan Median,
    TimeSpan Percentile95,
    TimeSpan Maximum);

/// <summary>
/// Collects per-frame durations for one run. Holds at most <see cref="Capacity"/> samples; later
/// ones are counted in <see cref="Overflowed"/> and left out of the summary. Not thread-safe.
/// </summary>
internal sealed class DurationSeries(int capacity = DurationSeries.DefaultCapacity)
{
    /// <summary>Enough for the longest permitted run (600 s at 240 frames per second).</summary>
    public const int DefaultCapacity = 144_000;

    private readonly List<long> _ticks = [];

    public int Capacity { get; } = capacity;

    public int Count => _ticks.Count;

    public long Overflowed { get; private set; }

    public void Add(TimeSpan duration)
    {
        if (_ticks.Count >= Capacity)
        {
            Overflowed++;
            return;
        }

        _ticks.Add(duration.Ticks);
    }

    /// <summary>Null when nothing was recorded, so that an absent measurement is never shown as zero.</summary>
    public DurationSummary? Summarize()
    {
        if (_ticks.Count == 0)
        {
            return null;
        }

        long[] sorted = [.. _ticks];
        Array.Sort(sorted);

        return new DurationSummary(
            sorted.Length,
            TimeSpan.FromTicks((long)sorted.Average()),
            TimeSpan.FromTicks(sorted[0]),
            TimeSpan.FromTicks(Percentile(sorted, 50)),
            TimeSpan.FromTicks(Percentile(sorted, 95)),
            TimeSpan.FromTicks(sorted[^1]));
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    internal static long Percentile(long[] sorted, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
