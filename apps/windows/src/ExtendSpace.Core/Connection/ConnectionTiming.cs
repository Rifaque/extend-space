using System.Diagnostics;

namespace ExtendSpace.Core.Connection;

// Taken from the experiment's Streaming/Timing.cs for the production host: the monotonic clock
// that video timestamps and pings are on, the clock-offset estimate, and the keyframe request limit.
// ReconnectBackoff is not here: the coordinator's RetryBackoff replaced it in stage 2.

/// <summary>
/// The host's monotonic clock in microseconds: the Windows performance counter, which is also what
/// Windows.Graphics.Capture stamps frames with. Video timestamps and ping times are both on it.
/// This is time inside the connection (keepalives, pings), not the coordinator's session clock.
/// </summary>
internal static class MonotonicClock
{
    public static long NowMicros => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
}

/// <param name="OffsetMicros">Client clock minus host clock.</param>
/// <param name="RoundTripMicros">The round trip it was measured over. The offset is good to half of this.</param>
public readonly record struct ClockOffset(long OffsetMicros, long RoundTripMicros);

/// <summary>
/// Estimates how far the client's monotonic clock is from the host's, from ping and pong (protocol
/// section 11). A ping leaves at t0 and its pong returns at t3 carrying the client's clock c; taking
/// the reply to have been written halfway, the client read c at host time (t0 + t3) / 2, good to half
/// the round trip. The sample with the shortest round trip among the recent ones is kept.
/// </summary>
internal sealed class ClockOffsetEstimator(int window = 16)
{
    private readonly Lock _gate = new();
    private readonly Queue<ClockOffset> _samples = new(window);
    private ClockOffset? _best;
    private long _count;

    public long Samples
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public ClockOffset? Best
    {
        get
        {
            lock (_gate)
            {
                return _best;
            }
        }
    }

    /// <summary>Returns false, and changes nothing, for a pong that claims to have arrived before its ping left.</summary>
    public bool Add(long hostSendMicros, long hostReceiveMicros, long clientMicros)
    {
        long roundTrip = hostReceiveMicros - hostSendMicros;
        if (roundTrip < 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_samples.Count == window)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(new ClockOffset(clientMicros - (hostSendMicros + (roundTrip / 2)), roundTrip));
            _best = _samples.MinBy(sample => sample.RoundTripMicros);
            _count++;
        }

        return true;
    }
}

/// <summary>
/// Who asked the host's gate for a keyframe (D62). Only labels diagnostics: the gate treats every
/// source alike.
/// </summary>
internal enum KeyframeRequestSource
{
    /// <summary>The client sent <c>request_keyframe</c>.</summary>
    Client,

    /// <summary>
    /// The send queue overflowed and is waiting for a keyframe. Since D64 asked by the writer once it
    /// is free again, not at the overflow.
    /// </summary>
    QueueFull,

    /// <summary>
    /// A frame was refused because the send queue is waiting for a keyframe. The controller no
    /// longer makes this request (D64): the keyframe such a frame waits for is always owed already.
    /// Kept for the gate's own tests and diagnostics names.
    /// </summary>
    AwaitingKeyframe,

    /// <summary>An access unit was too large for one video frame.</summary>
    TooLarge,
}

internal enum KeyframeGateDecision
{
    /// <summary>Passed on now.</summary>
    Passed,

    /// <summary>Too soon after the last one passed on: deferred, and no other was waiting.</summary>
    Deferred,

    /// <summary>Too soon, and a deferred request was already waiting: merged into it.</summary>
    Coalesced,
}

/// <summary>What the gate did with one request (D62).</summary>
/// <param name="SinceLastPassMicros">For a request passed on: since the previous one was; null for the first.</param>
/// <param name="DueMicros">For a deferred or merged request: when the waiting request falls due.</param>
/// <param name="Absorbed">For a request passed on: the deferred request that was waiting and is answered by this one instead of being released.</param>
internal readonly record struct KeyframeGateResult(
    KeyframeGateDecision Decision,
    long? SinceLastPassMicros,
    long? DueMicros,
    KeyframeGatePending? Absorbed)
{
    public bool Passed => Decision == KeyframeGateDecision.Passed;
}

/// <summary>A deferred request: the requests merged into it, by source, and when the first of them came.</summary>
internal readonly record struct KeyframeGatePending(
    long SinceMicros,
    long DueMicros,
    KeyframeRequestSource FirstSource,
    int ClientRequests,
    int HostRequests);

/// <summary>
/// Passes on at most one keyframe request per interval (protocol section 10: about two a second),
/// and defers rather than discards one that comes too soon: it is passed on when the interval ends.
/// A request is never lost, because the client asks only when a frame arrives, and on a still
/// screen none may (architecture section 5.4). Thread-safe.
///
/// <para>D62 added only bookkeeping for diagnostics: who asked, and what each request became. When a
/// request is passed on and what is deferred are exactly as before.</para>
/// </summary>
internal sealed class KeyframeRequestGate(TimeSpan minimumInterval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly long _intervalMicros = (long)minimumInterval.TotalMicroseconds;
    private long? _lastPassedMicros;
    private bool _deferred;

    // Diagnostics only (D62), under _gate: the requests merged into the deferred one.
    private long _deferredSinceMicros;
    private KeyframeRequestSource _deferredFirstSource;
    private int _deferredClient;
    private int _deferredHost;

    /// <summary>
    /// True if the request is to be passed on now. False if one was passed on too recently: the
    /// request is then deferred until <see cref="DeferredDueMicros"/>, and many deferred requests
    /// are one.
    /// </summary>
    public bool Request(long nowMicros) => Request(nowMicros, KeyframeRequestSource.Client).Passed;

    /// <summary><see cref="Request(long)"/>, reporting what became of the request and who it came from.</summary>
    public KeyframeGateResult Request(long nowMicros, KeyframeRequestSource source)
    {
        lock (_gate)
        {
            if (_lastPassedMicros is { } last && nowMicros - last < _intervalMicros)
            {
                bool merged = _deferred;
                if (!merged)
                {
                    _deferredSinceMicros = nowMicros;
                    _deferredFirstSource = source;
                    _deferredClient = 0;
                    _deferredHost = 0;
                }

                Count(source);
                _deferred = true;
                return new KeyframeGateResult(merged ? KeyframeGateDecision.Coalesced : KeyframeGateDecision.Deferred, null, last + _intervalMicros, null);
            }

            KeyframeGatePending? absorbed = _deferred ? Pending() : null;
            long? since = _lastPassedMicros is { } previous ? nowMicros - previous : null;
            _lastPassedMicros = nowMicros;
            _deferred = false;
            return new KeyframeGateResult(KeyframeGateDecision.Passed, since, null, absorbed);
        }
    }

    /// <summary>Forgets the last request passed on and any deferred one: for a new stream (D64).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastPassedMicros = null;
            _deferred = false;
        }
    }

    /// <summary>When a deferred request falls due; null when none is waiting.</summary>
    public long? DeferredDueMicros
    {
        get
        {
            lock (_gate)
            {
                return _deferred && _lastPassedMicros is { } last ? last + _intervalMicros : null;
            }
        }
    }

    /// <summary>True, once, when a deferred request has fallen due and is to be passed on now.</summary>
    public bool TakeDue(long nowMicros) => TakeDue(nowMicros, out _);

    /// <summary><see cref="TakeDue(long)"/>, reporting the deferred request that was released.</summary>
    public bool TakeDue(long nowMicros, out KeyframeGatePending released)
    {
        lock (_gate)
        {
            if (!_deferred || (_lastPassedMicros is { } last && nowMicros - last < _intervalMicros))
            {
                released = default;
                return false;
            }

            released = Pending();
            _deferred = false;
            _lastPassedMicros = nowMicros;
            return true;
        }
    }

    // Under _gate, while a request is deferred.
    private KeyframeGatePending Pending() =>
        new(_deferredSinceMicros, (_lastPassedMicros ?? _deferredSinceMicros) + _intervalMicros, _deferredFirstSource, _deferredClient, _deferredHost);

    private void Count(KeyframeRequestSource source)
    {
        if (source == KeyframeRequestSource.Client)
        {
            _deferredClient++;
        }
        else
        {
            _deferredHost++;
        }
    }
}
