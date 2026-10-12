namespace ExtendSpace.Experiments.CaptureEncode.Measurement;

/// <summary>
/// Decides which captured frames go on to the encoder so that the accepted frames average the
/// target rate, even when the display refreshes faster or not at a multiple of it. Not thread-safe.
/// </summary>
internal sealed class FramePacer
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _tolerance;
    private TimeSpan? _nextDue;

    public FramePacer(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _interval = interval;

        // A frame that is this close to being due is taken now rather than a whole refresh later.
        _tolerance = interval / 8;
    }

    /// <param name="timestamp">The frame's capture time on any monotonic clock.</param>
    public bool ShouldAccept(TimeSpan timestamp)
    {
        if (_nextDue is not { } due)
        {
            _nextDue = timestamp + _interval;
            return true;
        }

        if (timestamp + _tolerance < due)
        {
            return false;
        }

        // Keep the long-run average on target, but never bank a backlog after a quiet period.
        due += _interval;
        _nextDue = due <= timestamp ? timestamp + _interval : due;
        return true;
    }
}
