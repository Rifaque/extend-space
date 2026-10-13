using System.Net;
using System.Net.Sockets;
using ExtendSpace.Experiments.CaptureEncode.Streaming;
using ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Streaming;

/// <summary>
/// The host session against a stand-in client on a real loopback socket: the same connect,
/// handshake, send, receive and reconnect code that runs against the Android app, without a device.
/// What these cannot show is anything about adb, the tablet or video; that is tested on hardware.
/// </summary>
public sealed class HostSessionTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly StreamConfig Config = new("h264", "high", 1280, 720, 30, 6_000_000, Protocol.MaxVideoPayload, "Test Encoder", "software");

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<string> _log = [];
    private readonly List<IDisposable> _cleanup = [];

    public HostSessionTests()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>A free loopback port chosen by the system, kept even after the listener is stopped.</summary>
    private int Port { get; }

    public void Dispose()
    {
        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

        _listener.Stop();
    }

    [Fact]
    public void The_session_is_not_streaming_until_the_whole_handshake_has_succeeded()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();

        // Connected at the socket level, nothing exchanged yet.
        Assert.False(session.IsStreaming);

        client.Send(Protocol.Preface.ToArray());
        client.Send(new Hello(0, 0, "client", "Fake Pad", "9.9"));
        Assert.Equal(new Hello(0, 0, "host", "TEST-HOST", "1.2.3"), client.Expect<Hello>());
        Assert.False(session.IsStreaming);

        client.Send(Capabilities(4096, 4096));
        Assert.Equal(Config, client.Expect<StreamConfig>());
        Assert.False(session.IsStreaming);

        client.Send(new StreamReady("fake.decoder", true));
        WaitFor(() => session.IsStreaming);

        Assert.Equal(SessionPhase.Streaming, session.Phase);
        Assert.Equal("Fake Pad", session.ClientHello?.DeviceName);
        Assert.Equal("fake.decoder", session.ClientReady?.Decoder);
        Assert.Equal(1, session.Counters.ConnectionsEstablished);
    }

    [Fact]
    public void Video_starts_at_a_keyframe_and_arrives_in_order_with_its_sequence_and_timestamp()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);
        Assert.True(session.TakeKeyframeRequest(), "a new connection asks the encoder for a keyframe");

        session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 1_000);
        session.OfferAccessUnit(H264Samples.IdrAccessUnit, 2_000);
        session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 3_000);
        session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 4_000);

        var received = new List<(ushort Flags, uint Sequence, long Timestamp, byte[] AccessUnit)>();
        while (received.Count < 3)
        {
            ReceivedFrame frame = client.NextFrame();
            if (frame.Header.Channel == Protocol.ChannelVideo)
            {
                byte[] accessUnit = VideoFrameCodec.ReadVideoPayload(frame.Payload, out uint sequence, out long timestamp).ToArray();
                received.Add((frame.Header.Flags, sequence, timestamp, accessUnit));
            }
        }

        // The picture before the keyframe was never sent. It still used up sequence number 0:
        // every frame encoded for a connection is numbered, so that a frame dropped on the host
        // at any point shows up on the client as a gap.
        Assert.Equal([(ushort)3, (ushort)0, (ushort)0], received.Select(f => f.Flags));
        Assert.Equal([1u, 2u, 3u], received.Select(f => f.Sequence));
        Assert.Equal([2_000L, 3_000L, 4_000L], received.Select(f => f.Timestamp));
        Assert.Equal(H264Samples.IdrAccessUnit, received[0].AccessUnit);
        Assert.Equal(H264Samples.NonIdrAccessUnit, received[1].AccessUnit);

        WaitFor(() => session.Counters.Transmitted == 3);
        HostStreamCounters counters = session.Counters;
        Assert.Equal((4, 1, 3, 1), (counters.Encoded, counters.DroppedAwaitingKeyframe, counters.Transmitted, counters.KeyframesTransmitted));
        Assert.Equal(0, counters.Unaccounted);
    }

    [Fact]
    public void Frames_encoded_while_nobody_is_connected_are_counted_and_not_sent()
    {
        HostSession session = StartSession();

        session.OfferAccessUnit(H264Samples.IdrAccessUnit, 1);
        session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 2);

        Assert.Equal((2, 2, 0), (session.Counters.Encoded, session.Counters.NotSentNoClient, session.Counters.Transmitted));
        Assert.False(session.TakeKeyframeRequest());
    }

    [Fact]
    public void Pings_are_answered_and_give_the_host_a_clock_relation_which_it_then_shares()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();

        const long ClientClockAhead = 50_000_000;
        Ping? withOffset = null;
        for (int i = 0; i < 20 && withOffset is null; i++)
        {
            ReceivedFrame frame = client.NextFrame();
            if (ControlCodec.Decode(frame.Header.Type, frame.Payload) is Ping ping)
            {
                client.Send(new Pong(ping.Id, ping.HostTimeMicros, HostClock.NowMicros + ClientClockAhead));
                if (ping.ClockOffsetMicros is not null)
                {
                    withOffset = ping;
                }
            }
        }

        Assert.NotNull(withOffset);
        ClockOffset clock = Assert.NotNull(session.Clock);

        // Both ends are this process, so the true offset is exactly what the fake client added.
        Assert.InRange(clock.OffsetMicros, ClientClockAhead - (clock.RoundTripMicros / 2) - 1, ClientClockAhead + (clock.RoundTripMicros / 2) + 1);
        Assert.True(clock.RoundTripMicros < 1_000_000, $"round trip on loopback was {clock.RoundTripMicros} microseconds");
    }

    [Fact]
    public void A_keyframe_request_from_the_client_reaches_the_encoder_once_however_often_it_is_sent()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);
        Assert.True(session.TakeKeyframeRequest());
        Assert.False(session.TakeKeyframeRequest());

        Thread.Sleep(KeyframeRequestLimiter.DefaultInterval + TimeSpan.FromMilliseconds(100));
        for (int i = 0; i < 5; i++)
        {
            client.Send(new KeyframeRequest("decode_error"));
        }

        WaitFor(() => session.TakeKeyframeRequest());
        Assert.False(session.TakeKeyframeRequest());
        Assert.Equal(2, session.Counters.KeyframeRequests);
    }

    [Fact]
    public void Client_statistics_are_kept_and_summed_across_connections()
    {
        HostSession session = StartSession();
        using (FakeClient first = Accept())
        {
            first.Handshake();
            first.Send(new ClientStats { FramesReceived = 10, FramesDecoded = 10, FramesRendered = 9, RenderLatency = new LatencySummary(9, 10, 20, 30, 40) });
            WaitFor(() => session.LatestClientSample?.Stats.FramesReceived == 10);
            first.CloseAbruptly();
        }

        using FakeClient second = Accept();
        second.Handshake();
        second.Send(new ClientStats { FramesReceived = 5, FramesDecoded = 5, FramesRendered = 5 });
        WaitFor(() => session.LatestClientSample?.Stats.FramesReceived == 5);

        Assert.Equal((15, 15, 14), (session.ClientTotals.FramesReceived, session.ClientTotals.FramesDecoded, session.ClientTotals.FramesRendered));
        Assert.Equal(9, session.Latency.Render.Frames);
        Assert.Equal(20, session.Latency.Render.MeanMs);
    }

    [Fact]
    public void A_lost_connection_is_re_established_and_the_stream_restarts_from_a_keyframe()
    {
        HostSession session = StartSession();
        using (FakeClient first = Accept())
        {
            first.Handshake();
            WaitFor(() => session.IsStreaming);
            session.OfferAccessUnit(H264Samples.IdrAccessUnit, 1);
            session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 2);
            WaitFor(() => session.Counters.Transmitted == 2);
            first.CloseAbruptly();
        }

        WaitFor(() => session.Phase == SessionPhase.Reconnecting || session.Counters.ConnectionsLost == 1);
        Assert.False(session.IsStreaming);

        using FakeClient second = Accept();
        second.Handshake();
        WaitFor(() => session.IsStreaming);
        Assert.Equal((2, 1), (session.Counters.ConnectionsEstablished, session.Counters.ConnectionsLost));

        // Pictures that depend on what the first connection saw are useless to the second.
        session.OfferAccessUnit(H264Samples.NonIdrAccessUnit, 3);
        session.OfferAccessUnit(H264Samples.IdrAccessUnit, 4);

        ReceivedFrame video = second.NextVideoFrame();
        VideoFrameCodec.ReadVideoPayload(video.Payload, out uint sequence, out long timestamp);
        // Numbering started again for the new connection: 0 was the dropped picture, 1 is the keyframe.
        Assert.Equal(((ushort)3, 1u, 4L), (video.Header.Flags, sequence, timestamp));
        Assert.Equal(0, session.Counters.Unaccounted);
    }

    [Fact]
    public void A_client_that_says_goodbye_ends_the_session_without_a_reconnection_attempt()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        client.Send(new Bye("the user closed the screen"));

        WaitFor(() => session.HasEnded);
        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.Contains("the user closed the screen", session.EndReason);
        Assert.Equal(0, session.Counters.ConnectionsLost);
    }

    [Fact]
    public void Stopping_says_goodbye_with_the_reason_and_ends_disconnected()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        session.Stop("the requested duration elapsed");

        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Bye? bye = null;
        while (bye is null)
        {
            ReceivedFrame frame = client.NextFrame();
            bye = ControlCodec.Decode(frame.Header.Type, frame.Payload) as Bye;
        }

        Assert.Equal("the requested duration elapsed", bye.Reason);
    }

    [Fact]
    public void With_nothing_listening_the_session_fails_once_its_budget_is_spent()
    {
        _listener.Stop();
        HostSession session = StartSession(TimeSpan.FromSeconds(1));

        WaitFor(() => session.HasEnded);

        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Contains("no connection could be established within 1 s", session.EndReason);
        Assert.Equal(0, session.Counters.ConnectionsEstablished);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public void A_peer_that_accepts_and_closes_as_adb_does_with_no_app_behind_it_is_not_a_connection()
    {
        HostSession session = StartSession(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 3; i++)
        {
            using FakeClient client = Accept();
            client.CloseAbruptly();
        }

        Assert.False(session.IsStreaming);
        Assert.Equal(0, session.Counters.ConnectionsEstablished);

        // And when something does answer properly, it connects.
        using FakeClient real = Accept();
        real.Handshake();
        WaitFor(() => session.IsStreaming);
    }

    [Fact]
    public void A_peer_that_is_not_an_extendspace_client_fails_the_session_instead_of_being_retried()
    {
        HostSession session = StartSession();
        using FakeClient stranger = Accept();

        stranger.Send("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());

        WaitFor(() => session.HasEnded);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Contains(ErrorCode.MalformedMessage, session.EndReason);
    }

    [Fact]
    public void A_client_with_no_decoder_large_enough_is_told_so_and_the_session_fails()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Send(Protocol.Preface.ToArray());
        client.Send(new Hello(0, 0, "client", "Small Pad", "1"));
        client.Expect<Hello>();
        client.Send(Capabilities(640, 480));

        ErrorMessage error = client.Expect<ErrorMessage>();

        Assert.Equal(ErrorCode.NoCommonConfiguration, error.Code);
        Assert.True(error.Fatal);
        WaitFor(() => session.HasEnded);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public void A_client_from_an_incompatible_protocol_version_is_refused()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Send(Protocol.Preface.ToArray());
        client.Send(new Hello(1, 3, "client", "Future Pad", "5"));
        client.Expect<Hello>();

        Assert.Equal(ErrorCode.ProtocolVersionUnsupported, client.Expect<ErrorMessage>().Code);
        WaitFor(() => session.HasEnded);
        Assert.Equal(SessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void An_oversized_frame_from_the_client_ends_the_session_with_an_error_not_a_crash()
    {
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        client.Send(FrameParserTests.Header(Protocol.ChannelControl, (byte)ControlType.Stats, 0, 0xFFFFFFFF));

        WaitFor(() => session.HasEnded);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Contains(ErrorCode.MessageTooLarge, session.EndReason);
    }

    private static ClientCapabilities Capabilities(int maxWidth, int maxHeight) =>
        new(3200, 2136, 120, [new DecoderCapability("fake.decoder", true, false, maxWidth, maxHeight)]);

    private HostSession StartSession(TimeSpan? reconnectBudget = null)
    {
        var session = new HostSession(
            new HostSessionOptions(Port, reconnectBudget ?? TimeSpan.FromSeconds(8), SendQueueCapacity: 4, "TEST-HOST", "1.2.3"),
            () => Config,
            message =>
            {
                lock (_log)
                {
                    _log.Add(message);
                }
            },
            () => { });
        _cleanup.Add(session);
        session.Start();
        return session;
    }

    private FakeClient Accept()
    {
        Task<Socket> accepting = _listener.AcceptSocketAsync();
        Assert.True(accepting.Wait(Patience), "the host did not connect");
        return new FakeClient(accepting.Result);
    }

    private void WaitFor(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                string log;
                lock (_log)
                {
                    log = string.Join(Environment.NewLine, _log);
                }

                Assert.Fail($"The expected state was not reached within {Patience.TotalSeconds:0} s. Session log:{Environment.NewLine}{log}");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>The Android app's side of the protocol, reduced to what these tests need.</summary>
    private sealed class FakeClient(Socket socket) : IDisposable
    {
        private readonly ConnectionReader _reader = new(socket);

        public void Send(byte[] bytes) => socket.Send(bytes);

        public void Send(object message) => socket.Send(ControlCodec.Encode(message));

        public void Handshake()
        {
            Send(Protocol.Preface.ToArray());
            Send(new Hello(0, 0, "client", "Fake Pad", "9.9"));
            Expect<Hello>();
            Send(Capabilities(4096, 4096));
            Expect<StreamConfig>();
            Send(new StreamReady("fake.decoder", true));
        }

        public T Expect<T>()
            where T : class => _reader.Expect<T>(Patience);

        public ReceivedFrame NextFrame()
        {
            Assert.True(_reader.TryNext(Patience, CancellationToken.None, out ReceivedFrame frame), "nothing arrived from the host");
            return frame;
        }

        public ReceivedFrame NextVideoFrame()
        {
            while (true)
            {
                ReceivedFrame frame = NextFrame();
                if (frame.Header.Channel == Protocol.ChannelVideo)
                {
                    return frame;
                }
            }
        }

        /// <summary>Drops the connection without a goodbye, as a pulled cable or a killed app would.</summary>
        public void CloseAbruptly() => socket.Close(0);

        public void Dispose() => socket.Dispose();
    }
}
