using System.Diagnostics;
using System.Globalization;

namespace ExtendSpace.Experiments.CaptureEncode.Pattern;

/// <summary>One second of the pattern's own account of how often it reached the screen.</summary>
/// <param name="Shortfall">How many pictures fewer than the display had refreshes for; null if the refresh rate is unknown.</param>
internal readonly record struct PresentSecond(double Seconds, int Presented, int? Shortfall, double LongestIntervalMs, int Occluded)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Seconds,7:0.0} s] pattern: presented {Presented,4}{(Shortfall is { } missing ? $", short by {missing,3}" : string.Empty)}, longest interval {LongestIntervalMs,6:0.00} ms{(Occluded > 0 ? $", NOT VISIBLE for {Occluded} presents" : string.Empty)}");
}

/// <summary>
/// Measures what the pattern actually did: how many pictures it presented in each second, against
/// how many the display had refreshes for. It is the evidence that the source really supplied what
/// it was meant to; nothing is assumed from settings. Timestamps are <see cref="Stopwatch"/> ticks.
/// Not thread-safe.
///
/// The count per second is what is judged, not the spacing. On a PC with monitors of different
/// refresh rates a present was measured to return on the faster monitor's beat even for a window
/// on the slower one, so the time between two presents says little about what reached the glass.
/// </summary>
/// <param name="expectedPerSecond">Pictures a second if none is missed: the refresh rate over the sync interval. Null if unknown.</param>
internal sealed class PresentTimeline(double? expectedPerSecond, long ticksPerSecond)
{
    private long? _start;
    private long? _previous;
    private long _secondEnd;
    private int _presentedThisSecond;
    private long _longestThisSecond;
    private int _occludedThisSecond;
    private int _seconds;

    public long Presented { get; private set; }

    /// <summary>
    /// Summed over the whole seconds seen, leaving the first out: how many pictures fewer than a
    /// full second's worth were presented. The first second is start-up.
    /// </summary>
    public long Shortfall { get; private set; }

    /// <summary>Whole seconds, after the first, in which fewer pictures were presented than the display could show.</summary>
    public int SecondsShort { get; private set; }

    public long Occluded { get; private set; }

    public long LongestIntervalTicks { get; private set; }

    public TimeSpan Elapsed => _start is { } start && _previous is { } last ? TimeSpan.FromSeconds((double)(last - start) / ticksPerSecond) : TimeSpan.Zero;

    /// <summary>A present that Windows did not put on screen because the window was covered or minimised.</summary>
    public void AddOccluded()
    {
        Occluded++;
        _occludedThisSecond++;
    }

    /// <summary>
    /// Records a present that returned at <paramref name="timestamp"/>. Returns the second that
    /// this present is the first one after, if it is: a present belongs to the second it falls in.
    /// </summary>
    public PresentSecond? Add(long timestamp)
    {
        _start ??= timestamp;
        if (_secondEnd == 0)
        {
            _secondEnd = timestamp + ticksPerSecond;
        }

        PresentSecond? completed = null;
        if (timestamp >= _secondEnd)
        {
            completed = CompleteSecond();

            // A gap longer than a second is reported once, in the second in which it ended.
            while (timestamp >= _secondEnd)
            {
                _secondEnd += ticksPerSecond;
            }
        }

        Presented++;
        _presentedThisSecond++;
        if (_previous is { } previous)
        {
            long interval = timestamp - previous;
            LongestIntervalTicks = Math.Max(LongestIntervalTicks, interval);
            _longestThisSecond = Math.Max(_longestThisSecond, interval);
        }

        _previous = timestamp;
        return completed;
    }

    private PresentSecond CompleteSecond()
    {
        // A display at 143.86 Hz shows 143 or 144 pictures in a second; only fewer than that is short.
        int? shortfall = expectedPerSecond is { } expected ? Math.Max(0, (int)Math.Floor(expected) - _presentedThisSecond) : null;
        _seconds++;
        if (_seconds > 1 && shortfall is > 0)
        {
            Shortfall += shortfall.Value;
            SecondsShort++;
        }

        var second = new PresentSecond(
            (double)(_secondEnd - _start!.Value) / ticksPerSecond,
            _presentedThisSecond,
            shortfall,
            _longestThisSecond * 1000.0 / ticksPerSecond,
            _occludedThisSecond);
        _presentedThisSecond = 0;
        _longestThisSecond = 0;
        _occludedThisSecond = 0;
        return second;
    }
}
