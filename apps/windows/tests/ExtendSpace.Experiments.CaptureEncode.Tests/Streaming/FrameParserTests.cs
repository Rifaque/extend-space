using ExtendSpace.Experiments.CaptureEncode.Streaming;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Streaming;

public class FrameParserTests
{
    private sealed record Parsed(FrameHeader Header, byte[] Payload);

    [Fact]
    public void A_stream_that_arrives_whole_yields_its_frames_in_order()
    {
        byte[] stream = [.. Protocol.Preface, .. Frame(0, 1, 0, [1, 2, 3]), .. Frame(1, 0, 3, Bytes(40)), .. Frame(0, 10, 0, [])];

        List<Parsed> frames = Parse(stream, stream.Length);

        Assert.Equal(3, frames.Count);
        Assert.Equal(new FrameHeader(0, 1, 0, 3), frames[0].Header);
        Assert.Equal([1, 2, 3], frames[0].Payload);
        Assert.Equal(new FrameHeader(1, 0, 3, 40), frames[1].Header);
        Assert.Equal(Bytes(40), frames[1].Payload);
        Assert.Equal(new FrameHeader(0, 10, 0, 0), frames[2].Header);
        Assert.Empty(frames[2].Payload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(64)]
    [InlineData(1000)]
    public void However_the_bytes_are_cut_up_the_frames_come_out_the_same(int chunk)
    {
        byte[] stream =
        [
            .. Protocol.Preface,
            .. Frame(0, 1, 0, Bytes(17)),
            .. Frame(1, 0, 1, Bytes(300)),
            .. Frame(1, 0, 0, Bytes(1)),
            .. Frame(0, 6, 0, []),
            .. Frame(1, 0, 2, Bytes(4096)),
        ];

        List<Parsed> expected = Parse(stream, stream.Length);
        List<Parsed> actual = Parse(stream, chunk);

        Assert.Equal(5, expected.Count);
        Assert.Equal(expected.Select(f => f.Header), actual.Select(f => f.Header));
        Assert.Equal(expected.Select(f => Convert.ToHexString(f.Payload)), actual.Select(f => Convert.ToHexString(f.Payload)));
    }

    [Fact]
    public void Several_messages_in_one_read_are_all_delivered()
    {
        var parser = new FrameParser();
        var types = new List<byte>();
        byte[] oneRead = [.. Protocol.Preface, .. Frame(0, 6, 0, [1]), .. Frame(0, 7, 0, [2]), .. Frame(0, 8, 0, [3]), .. Frame(0, 5, 0, [4])];

        parser.Append(oneRead, (header, _) => types.Add(header.Type));

        Assert.Equal([6, 7, 8, 5], types);
        Assert.Equal(4, parser.FramesParsed);
        Assert.True(parser.IsAtFrameBoundary);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(20)]
    public void A_stream_that_stops_short_is_recognised_as_truncated(int keep)
    {
        // Preface (5), header (8), ten payload bytes: anything shorter than all 23 is incomplete.
        byte[] whole = [.. Protocol.Preface, .. Frame(0, 1, 0, Bytes(10))];
        var parser = new FrameParser();
        int delivered = 0;

        parser.Append(whole.AsSpan(0, keep), (_, _) => delivered++);

        Assert.Equal(0, delivered);
        if (keep == 5)
        {
            // Exactly the preface and nothing else is a clean place to stop.
            Assert.True(parser.IsAtFrameBoundary);
            parser.Complete();
        }
        else
        {
            Assert.False(parser.IsAtFrameBoundary);
            Assert.Throws<ProtocolException>(parser.Complete);
        }
    }

    [Fact]
    public void A_complete_stream_ends_cleanly()
    {
        var parser = new FrameParser();
        parser.Append([.. Protocol.Preface, .. Frame(0, 10, 0, Bytes(4))], (_, _) => { });

        parser.Complete();
        Assert.True(parser.PrefaceReceived);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/' })]
    [InlineData(new byte[] { (byte)'E', (byte)'X', (byte)'S', (byte)'P', 1 })]
    [InlineData(new byte[] { (byte)'E', (byte)'X', (byte)'S', (byte)'Q' })]
    [InlineData(new byte[] { 0 })]
    public void Anything_but_the_preface_is_rejected_at_the_first_wrong_byte(byte[] opening)
    {
        var parser = new FrameParser();

        ProtocolException exception = Assert.Throws<ProtocolException>(() => parser.Append(opening, (_, _) => { }));

        Assert.Equal(ErrorCode.MalformedMessage, exception.Code);
        Assert.False(parser.PrefaceReceived);
    }

    [Theory]
    [InlineData(Protocol.ChannelControl, Protocol.MaxControlPayload)]
    [InlineData(Protocol.ChannelVideo, Protocol.MaxVideoPayload)]
    [InlineData(Protocol.ChannelInput, Protocol.MaxInputPayload)]
    public void A_frame_at_its_channel_limit_is_accepted_and_one_byte_more_is_not(byte channel, int limit)
    {
        var atLimit = new FrameParser();
        int delivered = 0;
        atLimit.Append([.. Protocol.Preface, .. Header(channel, 1, 0, (uint)limit)], (_, _) => delivered++);
        atLimit.Append(new byte[limit], (_, payload) => delivered += payload.Length == limit ? 1 : 0);
        Assert.Equal(1, delivered);

        var overLimit = new FrameParser();
        ProtocolException exception = Assert.Throws<ProtocolException>(
            () => overLimit.Append([.. Protocol.Preface, .. Header(channel, 1, 0, (uint)limit + 1)], (_, _) => { }));
        Assert.Equal(ErrorCode.MessageTooLarge, exception.Code);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0x7FFFFFFFu)]
    public void An_absurd_length_is_refused_from_the_header_alone_before_anything_is_allocated(uint length)
    {
        foreach (byte channel in new byte[] { 0, 1, 2, 3, 200 })
        {
            var parser = new FrameParser();
            long before = GC.GetAllocatedBytesForCurrentThread();

            ProtocolException exception = Assert.Throws<ProtocolException>(
                () => parser.Append([.. Protocol.Preface, .. Header(channel, 0, 0, length)], (_, _) => { }));

            Assert.Equal(ErrorCode.MessageTooLarge, exception.Code);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024, "the parser allocated for a frame it should have refused");
        }
    }

    [Fact]
    public void The_negotiated_video_limit_can_only_lower_the_default()
    {
        var lowered = new FrameParser(maxVideoPayload: 1000);
        Assert.Throws<ProtocolException>(() => lowered.Append([.. Protocol.Preface, .. Header(Protocol.ChannelVideo, 0, 0, 1001)], (_, _) => { }));

        var raised = new FrameParser(maxVideoPayload: int.MaxValue);
        Assert.Throws<ProtocolException>(
            () => raised.Append([.. Protocol.Preface, .. Header(Protocol.ChannelVideo, 0, 0, Protocol.MaxVideoPayload + 1)], (_, _) => { }));
    }

    [Fact]
    public void Frames_on_an_unknown_channel_are_skipped_and_what_follows_is_still_read()
    {
        byte[] stream =
        [
            .. Protocol.Preface,
            .. Frame(9, 77, 0xFFFF, Bytes(5000)),
            .. Frame(0, 1, 0, [42]),
            .. Frame(250, 1, 0, []),
            .. Frame(1, 0, 0, [7, 7]),
        ];

        foreach (int chunk in new[] { stream.Length, 1, 11, 4999 })
        {
            var parser = new FrameParser();
            var seen = new List<byte>();
            for (int offset = 0; offset < stream.Length; offset += chunk)
            {
                parser.Append(stream.AsSpan(offset, Math.Min(chunk, stream.Length - offset)), (header, _) => seen.Add(header.Channel));
            }

            Assert.Equal([0, 1], seen);
            Assert.Equal(2, parser.FramesSkipped);
            Assert.True(parser.IsAtFrameBoundary);
        }
    }

    [Fact]
    public void An_unknown_message_type_on_a_known_channel_is_delivered_for_the_caller_to_ignore()
    {
        var frames = Parse([.. Protocol.Preface, .. Frame(0, 200, 0, [1, 2])], 3);

        Parsed frame = Assert.Single(frames);
        Assert.Equal(200, frame.Header.Type);
        Assert.Null(ControlCodec.Decode(frame.Header.Type, frame.Payload));
    }

    [Fact]
    public void Header_fields_are_big_endian()
    {
        byte[] header = Header(1, 2, 0x0102, 0x00030405);

        Assert.Equal([1, 2, 0x01, 0x02, 0x00, 0x03, 0x04, 0x05], header);
        Assert.Equal(new FrameHeader(1, 2, 0x0102, 0x00030405), Protocol.ReadHeader(header));
    }

    [Fact]
    public void The_reused_payload_buffer_does_not_leak_one_frame_into_the_next()
    {
        byte[] stream = [.. Protocol.Preface, .. Frame(0, 1, 0, Bytes(100)), .. Frame(0, 1, 0, [9, 9, 9])];

        List<Parsed> frames = Parse(stream, 7);

        Assert.Equal([9, 9, 9], frames[1].Payload);
    }

    internal static byte[] Frame(byte channel, byte type, ushort flags, byte[] payload) =>
        [.. Header(channel, type, flags, (uint)payload.Length), .. payload];

    internal static byte[] Header(byte channel, byte type, ushort flags, uint length)
    {
        byte[] header = new byte[Protocol.HeaderLength];
        Protocol.WriteHeader(header, new FrameHeader(channel, type, flags, length));
        return header;
    }

    private static byte[] Bytes(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)((i * 31) + 7))];

    private static List<Parsed> Parse(byte[] stream, int chunk)
    {
        var parser = new FrameParser();
        var frames = new List<Parsed>();
        for (int offset = 0; offset < stream.Length; offset += chunk)
        {
            parser.Append(
                stream.AsSpan(offset, Math.Min(chunk, stream.Length - offset)),
                (header, payload) => frames.Add(new Parsed(header, payload.ToArray())));
        }

        return frames;
    }
}
