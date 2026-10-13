using System.Diagnostics;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>
/// The host's monotonic clock in microseconds: the Windows performance counter, which is also what
/// Windows.Graphics.Capture stamps frames with. Video timestamps and ping times are both on it.
/// </summary>
internal static class HostClock
{
    public static long NowMicros => ToMicros(Stopwatch.GetTimestamp());

    public static long ToMicros(long stopwatchTimestamp) =>
        (long)((Int128)stopwatchTimestamp * 1_000_000 / Stopwatch.Frequency);

    /// <summary>A capture timestamp (time since the performance counter's origin) in microseconds.</summary>
    public static long ToMicros(TimeSpan systemRelativeTime) => systemRelativeTime.Ticks / TimeSpan.TicksPerMicrosecond;
}

/// <param name="OffsetMicros">Client clock minus host clock.</param>
/// <param name="RoundTripMicros">The round trip it was measured over. The offset is good to half of this.</param>
internal readonly record struct ClockOffset(long OffsetMicros, long RoundTripMicros);

/// <summary>
/// Estimates how far the client's monotonic clock is from the host's, from ping and pong. The two
/// clocks have unrelated origins, so they cannot be compared directly.
///
/// A ping leaves the host at t0 and its pong arrives back at t3, carrying the client's clock c from
/// the moment it answered. If the trip took equally long each way, the client read c at host time
/// (t0 + t3) / 2. The error of that assumption cannot exceed half the round trip, so the sample
/// with the shortest round trip among the recent ones is the one to trust.
/// </summary>
internal sealed class ClockOffsetEstimator(int window = 16)
{
    private readonly Queue<ClockOffset> _samples = new(window);

    public ClockOffset? Best { get; private set; }

    public long Samples { get; private set; }

    /// <summary>Returns false, and changes nothing, for a pong that claims to have arrived before its ping left.</summary>
    public bool Add(long hostSendMicros, long hostReceiveMicros, long clientMicros)
    {
        long roundTrip = hostReceiveMicros - hostSendMicros;
        if (roundTrip < 0)
        {
            return false;
        }

        if (_samples.Count == window)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(new ClockOffset(clientMicros - (hostSendMicros + (roundTrip / 2)), roundTrip));
        Samples++;
        Best = _samples.MinBy(sample => sample.RoundTripMicros);
        return true;
    }
}

/// <summary>
/// How long to wait before each attempt to re-establish a lost connection: 250 ms, doubling, capped
/// at 4 s, and no attempt once the total budget has been used (protocol section 12).
/// </summary>
internal sealed class ReconnectBackoff(TimeSpan budget)
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private TimeSpan _next = InitialDelay;

    public int Attempts { get; private set; }

    /// <summary>
    /// The delay before the next attempt, or null when <paramref name="elapsedSinceLoss"/> has used
    /// up the budget and it is time to give up. The last delay is shortened to end on the budget.
    /// </summary>
    public TimeSpan? NextDelay(TimeSpan elapsedSinceLoss)
    {
        TimeSpan remaining = budget - elapsedSinceLoss;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan delay = _next < remaining ? _next : remaining;
        _next = _next * 2 < MaximumDelay ? _next * 2 : MaximumDelay;
        Attempts++;
        return delay;
    }

    public void Reset()
    {
        _next = InitialDelay;
        Attempts = 0;
    }
}

/// <summary>Lets through at most one keyframe request per interval, so that a storm of requests costs one keyframe.</summary>
internal sealed class KeyframeRequestLimiter(TimeSpan minimumInterval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private long? _lastMicros;

    public bool TryAcquire(long nowMicros)
    {
        if (_lastMicros is { } last && nowMicros - last < (long)minimumInterval.TotalMicroseconds)
        {
            return false;
        }

        _lastMicros = nowMicros;
        return true;
    }
}
