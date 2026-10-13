using System.Text;
using ExtendSpace.Experiments.CaptureEncode.Streaming;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Streaming;

public class ControlCodecTests
{
    /// <summary>
    /// The same bytes are decoded by the Android tests (ProtocolVectorsTest.kt). If the two
    /// implementations ever disagree about the wire format, one of these fails.
    /// </summary>
    internal const string HelloVector =
        "00010000000000627b2270726f746f636f6c5f6d696e223a302c2270726f746f636f6c5f6d6178223a302c22726f6c65223a22636c69656e74222c226465766963655f6e616d65223a225465737420506164222c226170705f76657273696f6e223a22302e312e30227d";

    internal const string PingVector =
        "00060000000000577b226964223a372c22686f73745f74696d655f7573223a3132333435363738393031322c22636c6f636b5f6f66667365745f7573223a2d353030303030303132332c22726f756e645f747269705f7573223a313834307d";

    internal const string VideoVector = "0100000300000012000000070000001cbe991a14000000016588";

    [Fact]
    public void The_shared_hello_vector_decodes()
    {
        (FrameHeader header, byte[] payload) = Split(HelloVector);

        Assert.Equal(new FrameHeader(Protocol.ChannelControl, (byte)ControlType.Hello, 0, 98), header);
        Assert.Equal(new Hello(0, 0, "client", "Test Pad", "0.1.0"), ControlCodec.Decode(header.Type, payload));
    }

    [Fact]
    public void The_shared_ping_vector_decodes()
    {
        (FrameHeader header, byte[] payload) = Split(PingVector);

        Assert.Equal((byte)ControlType.Ping, header.Type);
        Assert.Equal(new Ping(7, 123_456_789_012, -5_000_000_123, 1840), ControlCodec.Decode(header.Type, payload));
    }

    [Fact]
    public void The_shared_video_vector_decodes()
    {
        (FrameHeader header, byte[] payload) = Split(VideoVector);

        Assert.Equal(new FrameHeader(Protocol.ChannelVideo, 0, (ushort)(VideoFlags.Keyframe | VideoFlags.Config), 18), header);
        ReadOnlySpan<byte> accessUnit = VideoFrameCodec.ReadVideoPayload(payload, out uint sequence, out long timestamp);
        Assert.Equal(7u, sequence);
        Assert.Equal(123_456_789_012, timestamp);
        Assert.Equal([0, 0, 0, 1, 0x65, 0x88], accessUnit.ToArray());
    }

    [Fact]
    public void Every_message_survives_encoding_and_decoding()
    {
        object[] messages =
        [
            new Hello(0, 0, "host", "DESKTOP-TEST", "0.1.0"),
            new StreamConfig("h264", "high", 1920, 1080, 60, 12_000_000, Protocol.MaxVideoPayload, "Some Encoder MFT", "hardware"),
            new StreamConfig("h264", "main", 1280, 720, 30, 6_000_000, 1_000_000, null, null),
            new StreamReady("c2.example.avc.decoder", true),
            new StreamReady(null, null),
            new KeyframeRequest("surface_recreated"),
            new Ping(1, 5_000, null, null),
            new Ping(2, 6_000, -42, 1_500),
            new Pong(2, 6_000, 987_654_321),
            new ErrorMessage(ErrorCode.StreamConfigRejected, "no decoder for 8192 x 8192", true),
            new Bye("the requested duration elapsed"),
        ];

        foreach (object message in messages)
        {
            (FrameHeader header, byte[] payload) = Split(Convert.ToHexString(ControlCodec.Encode(message)));

            Assert.Equal(Protocol.ChannelControl, header.Channel);
            Assert.Equal((uint)payload.Length, header.Length);
            Assert.Equal(message, ControlCodec.Decode(header.Type, payload));
        }
    }

    [Fact]
    public void Capabilities_and_statistics_survive_encoding_and_decoding()
    {
        var capabilities = new ClientCapabilities(
            3200,
            2136,
            120,
            [new DecoderCapability("c2.vendor.avc.decoder", true, false, 4096, 4096), new DecoderCapability("c2.android.avc.decoder", false, false, 4096, 4096)]);
        var stats = new ClientStats
        {
            FramesReceived = 600,
            BytesReceived = 4_000_000,
            FramesMissing = 2,
            FramesDiscarded = 3,
            FramesSubmitted = 597,
            FramesDecoded = 596,
            FramesRendered = 596,
            FramesDisplayed = 590,
            DecoderErrors = 1,
            QueueDepth = 2,
            ReceiveLatency = new LatencySummary(60, 4.5, 6.25, 9.75, 12),
            RenderLatency = new LatencySummary(60, 14.5, 18.25, 25, 31.5),
        };

        var decodedCapabilities = Assert.IsType<ClientCapabilities>(Roundtrip(capabilities));
        Assert.Equal((3200, 2136, 120.0), (decodedCapabilities.DisplayWidth, decodedCapabilities.DisplayHeight, decodedCapabilities.RefreshHz));
        Assert.Equal(capabilities.Decoders, decodedCapabilities.Decoders);

        var decodedStats = Assert.IsType<ClientStats>(Roundtrip(stats));
        Assert.Equal(stats, decodedStats);
        Assert.Null(decodedStats.DisplayLatency);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        byte[] json = Json("""{"reason":"decode_error","added_in_a_later_version":{"x":[1,2,3]},"n":1}""");

        Assert.Equal(new KeyframeRequest("decode_error"), ControlCodec.Decode((byte)ControlType.RequestKeyframe, json));
    }

    [Fact]
    public void An_unknown_control_type_is_not_an_error()
    {
        Assert.Null(ControlCodec.Decode(0, Json("{}")));
        Assert.Null(ControlCodec.Decode(11, Json("""{"anything":true}""")));
        Assert.Null(ControlCodec.Decode(255, [0xFF, 0xFE]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("""{"id":1""")]
    [InlineData("""{"id":"one","host_time_us":2}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"id":1.5,"host_time_us":2}""")]
    [InlineData("""{"id":99999999999999999999,"host_time_us":2}""")]
    public void A_ping_that_is_not_what_a_ping_must_be_is_malformed(string json)
    {
        ProtocolException exception = Assert.Throws<ProtocolException>(() => ControlCodec.Decode((byte)ControlType.Ping, Json(json)));
        Assert.Equal(ErrorCode.MalformedMessage, exception.Code);
    }

    [Fact]
    public void Json_nested_deeper_than_eight_levels_is_rejected()
    {
        // Depth counts the outermost object as one.
        static string Nested(int depth) =>
            "{\"reason\":\"x\",\"n\":" + string.Concat(Enumerable.Repeat("{\"n\":", depth - 1)) + "1" + new string('}', depth);

        string eight = Nested(8);
        string nine = Nested(9);

        Assert.NotNull(ControlCodec.Decode((byte)ControlType.RequestKeyframe, Json(eight)));
        Assert.Throws<ProtocolException>(() => ControlCodec.Decode((byte)ControlType.RequestKeyframe, Json(nine)));
    }

    [Fact]
    public void Invalid_utf8_is_malformed_not_a_crash()
    {
        // {"reason":"<an invalid two-byte sequence>"}
        byte[] json = [0x7B, 0x22, 0x72, 0x65, 0x61, 0x73, 0x6F, 0x6E, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D];

        Assert.Throws<ProtocolException>(() => ControlCodec.Decode((byte)ControlType.Bye, json));
    }

    [Fact]
    public void A_decoder_for_another_codec_is_left_out_rather_than_misread()
    {
        byte[] json = Json(
            """{"display":{"width":1,"height":1},"decoders":[{"codec":"hevc","name":"h","hardware":true,"max_width":8192,"max_height":8192},{"codec":"h264","name":"a","hardware":false,"max_width":1920,"max_height":1088}]}""");

        var capabilities = Assert.IsType<ClientCapabilities>(ControlCodec.Decode((byte)ControlType.Capabilities, json));

        DecoderCapability decoder = Assert.Single(capabilities.Decoders);
        Assert.Equal("a", decoder.Name);
    }

    [Fact]
    public void Only_control_messages_can_be_encoded()
    {
        Assert.Throws<ArgumentException>(() => ControlCodec.Encode("hello"));
    }

    private static object? Roundtrip(object message)
    {
        (FrameHeader header, byte[] payload) = Split(Convert.ToHexString(ControlCodec.Encode(message)));
        return ControlCodec.Decode(header.Type, payload);
    }

    private static (FrameHeader Header, byte[] Payload) Split(string hex)
    {
        byte[] frame = Convert.FromHexString(hex);
        return (Protocol.ReadHeader(frame), frame[Protocol.HeaderLength..]);
    }

    private static byte[] Json(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
