using ExtendSpace.Core.Protocol;

namespace ExtendSpace.Core.Connection;

// Copied from the experiment's Streaming/VideoSendQueue.cs for the production host; the experiment keeps its own
// copy until stage 3 has been validated on a device (architecture section 12, stage 3).

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
/// <param name="KeptKeyframe">
/// For <see cref="EnqueueOutcome.DroppedQueueFull"/>: the queue still holds a keyframe and the frames
/// that follow it, which will be sent; only what came after them was dropped (D64).
/// </param>
internal readonly record struct EnqueueResult(EnqueueOutcome Outcome, int AlsoDropped, bool KeptKeyframe = false)
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
/// <para><b>Overflow (D64).</b> A keyframe arriving at a full queue replaces everything in it. Any
/// other frame arriving at a full queue first drops the frames queued before the newest queued
/// keyframe, which nothing that follows depends on; if that made room, the frame is queued and the
/// picture goes on unbroken. Otherwise the keyframe and the frames after it are kept, in order,
/// and only the new frame (and everything after it, until a keyframe) is dropped. With no keyframe
/// queued, everything is dropped, as before. So a queued keyframe is lost only to a newer one or to
/// the connection's end, never to an overflow, and what is sent is always decodable: after any gap
/// the next frame sent is a keyframe.</para>
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
        bool keptKeyframe = false;
        EnqueueOutcome outcome;
        lock (_gate)
        {
            if (_awaitingKeyframe && !frame.IsRandomAccessPoint)
            {
                outcome = EnqueueOutcome.DroppedAwaitingKeyframe;
            }
            else if (_frames.Count < Capacity)
            {
                _awaitingKeyframe = false;
                _frames.Enqueue(frame);
                outcome = EnqueueOutcome.Queued;
            }
            else if (frame.IsRandomAccessPoint)
            {
                // A keyframe needs nothing that is queued: it starts the stream again.
                _awaitingKeyframe = false;
                alsoDropped = DiscardAll();
                _frames.Enqueue(frame);
                outcome = EnqueueOutcome.Queued;
            }
            else if (DiscardBeforeNewestKeyframe() is { } droppedBefore)
            {
                // The newest queued keyframe and what follows it are kept; what preceded it is not
                // needed by anything after it.
                alsoDropped = droppedBefore;
                if (_frames.Count < Capacity)
                {
                    _frames.Enqueue(frame);
                    outcome = EnqueueOutcome.Queued;
                }
                else
                {
                    // Still full of the keyframe and its dependants: those are sent, this frame and
                    // everything after it wait for the next keyframe.
                    _awaitingKeyframe = true;
                    keptKeyframe = true;
                    outcome = EnqueueOutcome.DroppedQueueFull;
                }
            }
            else
            {
                alsoDropped = DiscardAll();
                _awaitingKeyframe = true;
                outcome = EnqueueOutcome.DroppedQueueFull;
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

        return new EnqueueResult(outcome, alsoDropped, keptKeyframe);
    }

    /// <summary>
    /// Refuses frames from now until the next keyframe, keeping what is queued: for a frame that was
    /// dropped before it reached the queue, which the frames after it depend on.
    /// </summary>
    public void AwaitKeyframe()
    {
        lock (_gate)
        {
            _awaitingKeyframe = true;
        }
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

    /// <summary>
    /// As <see cref="TryDequeue"/>, without holding a thread while it waits. False when nothing came
    /// within <paramref name="timeout"/>; throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async ValueTask<OutgoingVideoFrame?> TryDequeueAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        if (!await _available.WaitAsync(timeout, cancellation).ConfigureAwait(false))
        {
            return null;
        }

        lock (_gate)
        {
            return _frames.TryDequeue(out OutgoingVideoFrame? frame) ? frame : null;
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

    // Under _gate. Drops the frames ahead of the newest queued keyframe and returns how many; null,
    // dropping nothing, when no keyframe is queued. Frames are only ever taken from the front, so
    // the order of what stays is unchanged.
    private int? DiscardBeforeNewestKeyframe()
    {
        int index = 0;
        int newest = -1;
        foreach (OutgoingVideoFrame queued in _frames)
        {
            if (queued.IsRandomAccessPoint)
            {
                newest = index;
            }

            index++;
        }

        if (newest < 0)
        {
            return null;
        }

        for (int i = 0; i < newest; i++)
        {
            _frames.Dequeue().Release();
        }

        return newest;
    }
}
