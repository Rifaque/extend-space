namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

internal enum EnqueueOutcome
{
    /// <summary>The frame is waiting to be written.</summary>
    Queued,

    /// <summary>The queue was full. Everything in it and this frame were dropped; a keyframe is now needed.</summary>
    DroppedQueueFull,

    /// <summary>Dropped because nothing can be decoded until the next keyframe arrives.</summary>
    DroppedAwaitingKeyframe,
}

/// <param name="AlsoDropped">Older frames that were removed from the queue to make room, beyond the frame offered.</param>
internal readonly record struct EnqueueResult(EnqueueOutcome Outcome, int AlsoDropped)
{
    /// <summary>True when the encoder should be asked for a keyframe so that sending can resume.</summary>
    public bool NeedsKeyframe => Outcome != EnqueueOutcome.Queued;
}

/// <summary>
/// The few encoded frames between the encoder and the socket. It never grows past its capacity:
/// when the writer cannot keep up, the stale frames are thrown away and sending resumes at the next
/// keyframe, because a picture is useless without the ones it was predicted from. Latency is
/// bounded by the capacity; it cannot build up.
///
/// One thread offers frames and another takes them.
/// </summary>
internal sealed class VideoSendQueue(int capacity) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<OutgoingVideoFrame> _frames = new(capacity);
    private readonly SemaphoreSlim _available = new(0);
    private bool _awaitingKeyframe = true;

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>True while frames are being refused until a keyframe comes. A new queue starts this way.</summary>
    public bool AwaitingKeyframe
    {
        get
        {
            lock (_gate)
            {
                return _awaitingKeyframe;
            }
        }
    }

    /// <summary>Offers a frame. A frame that is not queued is released here; the caller must not use it again.</summary>
    public EnqueueResult Enqueue(OutgoingVideoFrame frame)
    {
        int alsoDropped = 0;
        EnqueueOutcome outcome;
        lock (_gate)
        {
            if (_awaitingKeyframe && !frame.IsRandomAccessPoint)
            {
                outcome = EnqueueOutcome.DroppedAwaitingKeyframe;
            }
            else
            {
                _awaitingKeyframe = false;
                if (_frames.Count >= Capacity)
                {
                    alsoDropped = DiscardAll();
                    if (frame.IsRandomAccessPoint)
                    {
                        // A keyframe needs nothing that was just thrown away: it starts the stream again.
                        _frames.Enqueue(frame);
                        outcome = EnqueueOutcome.Queued;
                    }
                    else
                    {
                        _awaitingKeyframe = true;
                        outcome = EnqueueOutcome.DroppedQueueFull;
                    }
                }
                else
                {
                    _frames.Enqueue(frame);
                    outcome = EnqueueOutcome.Queued;
                }
            }
        }

        if (outcome == EnqueueOutcome.Queued)
        {
            _available.Release();
        }
        else
        {
            frame.Release();
        }

        return new EnqueueResult(outcome, alsoDropped);
    }

    /// <summary>Takes the oldest frame, waiting up to <paramref name="timeout"/> for one. The caller releases it.</summary>
    public bool TryDequeue(TimeSpan timeout, out OutgoingVideoFrame? frame)
    {
        frame = null;
        if (!_available.Wait(timeout))
        {
            return false;
        }

        lock (_gate)
        {
            // The count can be ahead of the queue after DiscardAll; such a permit is simply spent.
            return _frames.TryDequeue(out frame);
        }
    }

    /// <summary>Empties the queue and refuses frames until the next keyframe. Returns how many were discarded.</summary>
    public int Reset()
    {
        lock (_gate)
        {
            _awaitingKeyframe = true;
            return DiscardAll();
        }
    }

    public void Dispose()
    {
        Reset();
        _available.Dispose();
    }

    private int DiscardAll()
    {
        int discarded = _frames.Count;
        while (_frames.TryDequeue(out OutgoingVideoFrame? stale))
        {
            stale.Release();
        }

        return discarded;
    }
}
