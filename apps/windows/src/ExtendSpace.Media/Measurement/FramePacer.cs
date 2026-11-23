namespace ExtendSpace.Media.Measurement;

/// <summary>
/// Decides which captured frames go on to the encoder so that the accepted frames average the
/// target rate, even when the display refreshes faster or not at a multiple of it. Not thread-safe.
///
/// It is a token bucket: one token accrues per frame interval and accepting a frame spends one.
/// The bucket holds two tokens, so a frame that arrives late does not cost the frame after it.
/// Without that slack, a source running at about the target rate but landing on display refreshes
/// (14 ms, 21 ms, 14 ms ... for 60 fps content on a 143 Hz monitor) lost a frame every time one
/// came early, and delivered well under the target; that was measured. The price is that after a
/// pause one extra frame may be taken straight away.
/// </summary>
internal sealed class FramePacer
{
    /// <summary>One spare token: enough to ride out a late frame, and no more than one extra frame after a pause.</summary>
    private const double Capacity = 2;

    /// <summary>A frame this close to being due is taken now rather than a whole refresh later.</summary>
    private const double Tolerance = 1.0 / 8;

    private readonly TimeSpan _interval;
    private double _tokens = 1;
    private TimeSpan? _last;

    public FramePacer(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _interval = interval;
    }

    /// <param name="timestamp">The frame's capture time on any monotonic clock.</param>
    public bool ShouldAccept(TimeSpan timestamp)
    {
        if (_last is { } last && timestamp > last)
        {
            _tokens = Math.Min(Capacity, _tokens + ((timestamp - last) / _interval));
        }

        _last = timestamp;
        if (_tokens + Tolerance < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}
