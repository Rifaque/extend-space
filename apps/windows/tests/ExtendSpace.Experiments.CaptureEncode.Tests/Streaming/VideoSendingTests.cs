using ExtendSpace.Experiments.CaptureEncode.Streaming;
using ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Streaming;

public class VideoSendingTests
{
    private static readonly byte[] ParameterSets = H264Samples.Join(H264Samples.Sps, H264Samples.Pps);

    [Fact]
    public void A_video_frame_is_a_header_the_sequence_the_timestamp_and_then_the_access_unit()
    {
        byte[] accessUnit = H264Samples.NonIdrAccessUnit;

        OutgoingVideoFrame frame = OutgoingVideoFrame.Create(0x01020304, 0x0A0B0C0D0E0F1011, VideoFlags.None, default, accessUnit);
        byte[] bytes = frame.Bytes.ToArray();
        frame.Release();

        Assert.Equal(Protocol.HeaderLength + Protocol.VideoHeaderLength + accessUnit.Length, bytes.Length);
        Assert.Equal(new FrameHeader(Protocol.ChannelVideo, 0, 0, (uint)(Protocol.VideoHeaderLength + accessUnit.Length)), Protocol.ReadHeader(bytes));
        Assert.Equal([1, 2, 3, 4, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11], bytes[8..20]);
        Assert.Equal(accessUnit, bytes[20..]);
    }

    [Fact]
    public void What_is_built_is_what_the_parser_reads_back()
    {
        OutgoingVideoFrame frame = OutgoingVideoFrame.Create(41, 9_000_000_123, VideoFlags.Keyframe | VideoFlags.Config, ParameterSets, H264Samples.Join(H264Samples.IdrSlice));
        var parser = new FrameParser();
        FrameHeader header = default;
        byte[] payload = [];

        parser.Append([.. Protocol.Preface, .. frame.Bytes], (h, p) => (header, payload) = (h, p.ToArray()));
        frame.Release();

        Assert.Equal((ushort)3, header.Flags);
        byte[] accessUnit = VideoFrameCodec.ReadVideoPayload(payload, out uint sequence, out long timestamp).ToArray();
        Assert.Equal((41u, 9_000_000_123L), (sequence, timestamp));
        Assert.Equal([.. ParameterSets, .. H264Samples.Join(H264Samples.IdrSlice)], accessUnit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(12)]
    public void A_video_payload_too_short_for_its_header_and_any_picture_is_malformed(int length)
    {
        ProtocolException exception = Assert.Throws<ProtocolException>(() => VideoFrameCodec.ReadVideoPayload(new byte[length], out _, out _));
        Assert.Equal(ErrorCode.MalformedMessage, exception.Code);
    }

    [Fact]
    public void A_keyframe_with_its_own_parameter_sets_is_a_random_access_point_as_it_is()
    {
        var tracker = new ParameterSetTracker();

        VideoFlags flags = tracker.Classify(H264Samples.IdrAccessUnit, out ReadOnlySpan<byte> prepend);

        Assert.Equal(VideoFlags.Keyframe | VideoFlags.Config, flags);
        Assert.True(prepend.IsEmpty);
        Assert.True(tracker.HasParameterSets);
    }

    [Fact]
    public void A_keyframe_without_parameter_sets_gets_the_last_ones_seen_put_in_front()
    {
        var tracker = new ParameterSetTracker();
        tracker.Classify(H264Samples.IdrAccessUnit, out _);

        byte[] bare = H264Samples.Join(H264Samples.AccessUnitDelimiter, H264Samples.IdrSlice);
        VideoFlags flags = tracker.Classify(bare, out ReadOnlySpan<byte> prepend);

        Assert.Equal(VideoFlags.Keyframe | VideoFlags.Config, flags);
        Assert.Equal(ParameterSets, prepend.ToArray());
    }

    [Fact]
    public void A_keyframe_before_any_parameter_sets_were_seen_is_not_claimed_to_be_decodable()
    {
        var tracker = new ParameterSetTracker();

        VideoFlags flags = tracker.Classify(H264Samples.Join(H264Samples.IdrSlice), out ReadOnlySpan<byte> prepend);

        Assert.Equal(VideoFlags.Keyframe, flags);
        Assert.True(prepend.IsEmpty);
        Assert.False(tracker.HasParameterSets);
    }

    [Fact]
    public void Ordinary_pictures_carry_no_flags_and_need_nothing_added()
    {
        var tracker = new ParameterSetTracker();
        tracker.Classify(H264Samples.IdrAccessUnit, out _);

        Assert.Equal(VideoFlags.None, tracker.Classify(H264Samples.NonIdrAccessUnit, out ReadOnlySpan<byte> prepend));
        Assert.True(prepend.IsEmpty);
    }

    [Fact]
    public void Newer_parameter_sets_replace_older_ones()
    {
        var tracker = new ParameterSetTracker();
        tracker.Classify(H264Samples.IdrAccessUnit, out _);
        byte[] newSps = [0x67, 77, 0x40, 31, 0x95, 0xA0];
        tracker.Classify(H264Samples.Join(newSps, H264Samples.Pps, H264Samples.IdrSlice), out _);

        tracker.Classify(H264Samples.Join(H264Samples.IdrSlice), out ReadOnlySpan<byte> prepend);

        Assert.Equal(H264Samples.Join(newSps, H264Samples.Pps), prepend.ToArray());
    }

    [Fact]
    public void A_new_queue_takes_nothing_until_a_keyframe_arrives()
    {
        using var queue = new VideoSendQueue(4);
        Assert.True(queue.AwaitingKeyframe);

        Assert.Equal(new EnqueueResult(EnqueueOutcome.DroppedAwaitingKeyframe, 0), queue.Enqueue(Picture(0)));
        Assert.Equal(new EnqueueResult(EnqueueOutcome.Queued, 0), queue.Enqueue(Keyframe(1)));
        Assert.Equal(new EnqueueResult(EnqueueOutcome.Queued, 0), queue.Enqueue(Picture(2)));

        Assert.False(queue.AwaitingKeyframe);
        Assert.Equal([1u, 2u], Drain(queue));
    }

    [Fact]
    public void Frames_leave_in_the_order_they_arrived()
    {
        using var queue = new VideoSendQueue(8);
        queue.Enqueue(Keyframe(0));
        for (uint i = 1; i < 6; i++)
        {
            queue.Enqueue(Picture(i));
        }

        Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u], Drain(queue));
    }

    [Fact]
    public void A_full_queue_is_emptied_and_sending_waits_for_the_next_keyframe()
    {
        using var queue = new VideoSendQueue(3);
        queue.Enqueue(Keyframe(0));
        queue.Enqueue(Picture(1));
        queue.Enqueue(Picture(2));

        // The writer has stalled: the fourth frame finds the queue full.
        EnqueueResult overflow = queue.Enqueue(Picture(3));
        Assert.Equal(new EnqueueResult(EnqueueOutcome.DroppedQueueFull, 3), overflow);
        Assert.True(overflow.NeedsKeyframe);
        Assert.Equal(0, queue.Count);

        // Pictures that depend on what was thrown away are refused until a keyframe restarts things.
        Assert.Equal(EnqueueOutcome.DroppedAwaitingKeyframe, queue.Enqueue(Picture(4)).Outcome);
        Assert.Equal(EnqueueOutcome.DroppedAwaitingKeyframe, queue.Enqueue(Picture(5)).Outcome);
        Assert.Equal(EnqueueOutcome.Queued, queue.Enqueue(Keyframe(6)).Outcome);
        Assert.Equal(EnqueueOutcome.Queued, queue.Enqueue(Picture(7)).Outcome);
        Assert.Equal([6u, 7u], Drain(queue));
    }

    [Fact]
    public void A_keyframe_arriving_at_a_full_queue_replaces_what_was_in_it()
    {
        using var queue = new VideoSendQueue(2);
        queue.Enqueue(Keyframe(0));
        queue.Enqueue(Picture(1));

        EnqueueResult result = queue.Enqueue(Keyframe(2));

        Assert.Equal(new EnqueueResult(EnqueueOutcome.Queued, 2), result);
        Assert.False(result.NeedsKeyframe);
        Assert.Equal([2u], Drain(queue));
    }

    [Fact]
    public void The_queue_never_holds_more_than_its_capacity()
    {
        using var queue = new VideoSendQueue(4);
        int worst = 0;
        for (uint i = 0; i < 500; i++)
        {
            queue.Enqueue(i % 50 == 0 ? Keyframe(i) : Picture(i));
            worst = Math.Max(worst, queue.Count);
        }

        Assert.Equal(4, worst);
    }

    [Fact]
    public void Every_frame_offered_is_either_delivered_or_counted_as_dropped()
    {
        using var queue = new VideoSendQueue(4);
        long offered = 0;
        long dropped = 0;
        long delivered = 0;
        var random = new Random(12345);

        for (uint i = 0; i < 2000; i++)
        {
            EnqueueResult result = queue.Enqueue(i % 30 == 0 ? Keyframe(i) : Picture(i));
            offered++;
            dropped += result.AlsoDropped + (result.Outcome == EnqueueOutcome.Queued ? 0 : 1);

            // A writer that keeps up only some of the time.
            if (random.Next(3) == 0)
            {
                delivered += Drain(queue).Count;
            }
        }

        delivered += Drain(queue).Count;
        Assert.Equal(offered, delivered + dropped);
        Assert.True(dropped > 0 && delivered > 0);
    }

    [Fact]
    public void Resetting_discards_what_is_queued_and_waits_for_a_keyframe_again()
    {
        using var queue = new VideoSendQueue(4);
        queue.Enqueue(Keyframe(0));
        queue.Enqueue(Picture(1));

        Assert.Equal(2, queue.Reset());

        Assert.True(queue.AwaitingKeyframe);
        Assert.False(queue.TryDequeue(TimeSpan.Zero, out _));
        Assert.Equal(EnqueueOutcome.DroppedAwaitingKeyframe, queue.Enqueue(Picture(2)).Outcome);
    }

    [Fact]
    public void Taking_from_an_empty_queue_times_out_rather_than_blocking_forever()
    {
        using var queue = new VideoSendQueue(2);

        Assert.False(queue.TryDequeue(TimeSpan.FromMilliseconds(20), out OutgoingVideoFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public async Task A_waiting_taker_is_woken_by_a_frame_offered_from_another_thread()
    {
        using var queue = new VideoSendQueue(2);
        Task<uint?> taker = Task.Run(() =>
        {
            bool taken = queue.TryDequeue(TimeSpan.FromSeconds(10), out OutgoingVideoFrame? frame);
            uint? sequence = taken ? frame!.Sequence : null;
            frame?.Release();
            return sequence;
        });

        queue.Enqueue(Keyframe(77));

        Assert.Equal(77u, await taker.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_capacity_of_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoSendQueue(0));
    }

    private static OutgoingVideoFrame Keyframe(uint sequence) =>
        OutgoingVideoFrame.Create(sequence, sequence * 1000L, VideoFlags.Keyframe | VideoFlags.Config, default, H264Samples.IdrAccessUnit);

    private static OutgoingVideoFrame Picture(uint sequence) =>
        OutgoingVideoFrame.Create(sequence, sequence * 1000L, VideoFlags.None, default, H264Samples.NonIdrAccessUnit);

    private static List<uint> Drain(VideoSendQueue queue)
    {
        var sequences = new List<uint>();
        while (queue.TryDequeue(TimeSpan.Zero, out OutgoingVideoFrame? frame))
        {
            sequences.Add(frame!.Sequence);
            frame.Release();
        }

        return sequences;
    }
}
