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

    private TcpListener _listener = new(IPAddress.Loopback, 0);
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

    [Fact]
    public void Starting_a_session_twice_is_refused()
    {
        HostSession session = StartSession();

        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public void Stopping_before_anything_has_connected_ends_disconnected_at_once()
    {
        _listener.Stop();
        HostSession session = StartSession();
        WaitFor(() => Logged("not connected"));

        long took = TimeToStop(session, "cancelled before a client appeared");

        Assert.True(took < 1500, $"stopping took {took} ms");
        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.Equal("cancelled before a client appeared", session.EndReason);
        Assert.Equal(0, session.Counters.ConnectionsEstablished);
    }

    [Fact]
    public void Stopping_while_a_handshake_is_stalled_does_not_wait_for_the_handshake_to_time_out()
    {
        HostSession session = StartSession();

        // Something accepted the connection and then said nothing: the host is waiting for a hello.
        using FakeClient silent = Accept();
        WaitFor(() => session.Phase == SessionPhase.Negotiating);

        long took = TimeToStop(session, "cancelled during the handshake");

        Assert.True(took < 1500, $"stopping took {took} ms; the handshake step timeout is {Protocol.HandshakeStepTimeout.TotalSeconds:0} s");
        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.Equal("cancelled during the handshake", session.EndReason);
        Assert.Equal(0, session.Counters.ConnectionsEstablished);
        silent.WaitUntilClosedByThePeer();
    }

    [Fact]
    public void A_client_that_accepts_and_never_speaks_is_explained_as_possibly_busy_with_another_host()
    {
        HostSession session = StartSession(configure: options => options with { HandshakeStepTimeout = TimeSpan.FromMilliseconds(300) });

        // What the Android app does to a second host while it is serving a first: the connection
        // is made and then nothing comes.
        using FakeClient busy = Accept();
        busy.WaitUntilClosedByThePeer();

        WaitFor(() => Logged("another stream may already be connected"));
        Assert.Equal(0, session.Counters.ConnectionsEstablished);
        Assert.False(session.HasEnded, "a busy client is retried until the budget is spent, not failed at once");
    }

    [Fact]
    public void A_handshake_that_stalls_is_given_up_its_socket_closed_and_the_next_client_served()
    {
        HostSession session = StartSession(configure: options => options with { HandshakeStepTimeout = TimeSpan.FromMilliseconds(300) });
        using (FakeClient stalled = Accept())
        {
            stalled.Send(Protocol.Preface.ToArray());
            stalled.Send(new Hello(0, 0, "client", "Stalled Pad", "9.9"));

            // No capabilities follow. The host must not keep this half-made connection.
            stalled.WaitUntilClosedByThePeer();
        }

        Assert.False(session.IsStreaming);
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        // A connection that never finished its handshake was neither established nor lost.
        Assert.Equal((1, 0), (session.Counters.ConnectionsEstablished, session.Counters.ConnectionsLost));
        Assert.Equal("Fake Pad", session.ClientHello?.DeviceName);
    }

    [Fact]
    public void A_client_that_leaves_partway_through_the_handshake_is_retried_rather_than_fatal()
    {
        HostSession session = StartSession();
        using (FakeClient leaver = Accept())
        {
            leaver.Send(Protocol.Preface.ToArray());
            leaver.Send(new Hello(0, 0, "client", "Leaving Pad", "9.9"));
            leaver.Expect<Hello>();
            leaver.CloseAbruptly();
        }

        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        Assert.Equal((1, 0), (session.Counters.ConnectionsEstablished, session.Counters.ConnectionsLost));
    }

    [Fact]
    public void A_connection_that_ends_in_the_middle_of_a_message_is_lost_and_re_established()
    {
        HostSession session = StartSession();
        using (FakeClient first = Accept())
        {
            first.Handshake();
            WaitFor(() => session.IsStreaming);

            byte[] stats = ControlCodec.Encode(new ClientStats { FramesReceived = 3, FramesDecoded = 3, FramesRendered = 3 });
            first.Send(stats[..(stats.Length / 2)]);
            first.CloseAfterSending();
        }

        WaitFor(() => session.Counters.ConnectionsLost == 1);
        Assert.True(Logged("in the middle of a message"), "the loss should be described as an interrupted message");

        using FakeClient second = Accept();
        second.Handshake();
        WaitFor(() => session.IsStreaming);
        Assert.Equal(2, session.Counters.ConnectionsEstablished);

        // Half a statistics message is not statistics.
        Assert.Equal(0, session.ClientTotals.FramesReceived);
    }

    [Fact]
    public void A_client_that_keeps_talking_is_not_timed_out_however_long_the_connection_lasts()
    {
        HostSession session = StartSession(configure: options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(500) });
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);

        // Four timeouts' worth of time, with something arriving well inside each.
        for (int i = 0; i < 20; i++)
        {
            client.Send(new ClientStats { FramesReceived = i, FramesDecoded = i, FramesRendered = i });
            Thread.Sleep(100);
        }

        Assert.True(session.IsStreaming);
        Assert.Equal((1, 0), (session.Counters.ConnectionsEstablished, session.Counters.ConnectionsLost));
        Assert.False(Logged("nothing was received"), "a client that is sending must not be treated as silent");
    }

    [Fact]
    public void A_silent_client_that_has_also_stopped_reading_is_still_timed_out_and_its_socket_closed()
    {
        // What a frozen app looks like: nothing comes back and nothing is taken. The writer ends up
        // blocked, and the timeout has to end the connection all the same. Seen on a device.
        _listener.Server.ReceiveBufferSize = 8 * 1024;
        HostSession session = StartSession(configure: options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(600) });
        using FakeClient frozen = Accept();
        frozen.Handshake();
        WaitFor(() => session.IsStreaming);

        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        session.OfferAccessUnit(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 8; i++)
        {
            session.OfferAccessUnit(large, 2 + i);
        }

        // Two things run out at the same moment here, the wait for the client and the blocked
        // write, and either may be the one that reports the loss. Both are the silence being noticed.
        WaitFor(() => session.Counters.ConnectionsLost == 1 && Logged("connection lost"));
        string loss = LoggedLine("connection lost");
        Assert.True(
            loss.Contains("nothing was received from the client for 0.6 s", StringComparison.Ordinal) || loss.Contains("TimedOut", StringComparison.Ordinal),
            $"the loss was put down to something other than the silence: {loss}");
        frozen.WaitUntilClosedByThePeer();
        WaitFor(() => session.Counters.Unaccounted == 0);

        using FakeClient next = Accept();
        next.Handshake();
        WaitFor(() => session.Counters.ConnectionsEstablished == 2);
    }

    [Fact]
    public void A_client_that_goes_silent_is_lost_after_the_keepalive_timeout_and_the_host_connects_again()
    {
        HostSession session = StartSession(configure: options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(500) });
        using (FakeClient silent = Accept())
        {
            silent.Handshake();
            WaitFor(() => session.IsStreaming);

            // The socket stays open; nothing more is sent. Only the timeout can end this.
            WaitFor(() => session.Counters.ConnectionsLost == 1);
            Assert.True(Logged("nothing was received from the client for 0.5 s"), "the loss should be put down to silence");
            Assert.False(session.IsStreaming);
            silent.WaitUntilClosedByThePeer();
        }

        using FakeClient second = Accept();
        second.Handshake();
        WaitFor(() => session.Counters.ConnectionsEstablished == 2);
    }

    [Fact]
    public void A_client_that_stops_reading_costs_frames_not_memory_and_does_not_hold_up_stopping()
    {
        // Small receive buffers, so that the host's writes block after very little.
        _listener.Server.ReceiveBufferSize = 8 * 1024;
        HostSession session = StartSession();
        using FakeClient client = Accept();
        client.Handshake();
        WaitFor(() => session.IsStreaming);
        Assert.True(session.TakeKeyframeRequest());
        Thread.Sleep(KeyframeRequestLimiter.DefaultInterval + TimeSpan.FromMilliseconds(100));

        // From here the client reads nothing, and each picture is far larger than the socket can buffer.
        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        session.OfferAccessUnit(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 40; i++)
        {
            session.OfferAccessUnit(large, 2 + i);
        }

        HostStreamCounters blocked = session.Counters;
        Assert.InRange(session.QueueDepth, 0, 4);
        Assert.InRange(blocked.Unaccounted, 0, 5);
        Assert.True(blocked.DroppedQueueFull + blocked.DroppedAwaitingKeyframe >= 30, $"only {blocked.DroppedQueueFull + blocked.DroppedAwaitingKeyframe} of 41 frames were dropped");
        Assert.True(session.TakeKeyframeRequest(), "dropping encoded frames must ask the encoder for a keyframe");

        long took = TimeToStop(session, "stopped while the socket was full");

        Assert.True(took < 1500, $"stopping took {took} ms with a write blocked");
        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        WaitFor(() => session.Counters.Unaccounted == 0);
        Assert.Equal(41, session.Counters.Encoded);
    }

    [Fact]
    public void After_a_loss_the_transport_is_set_up_again_before_each_new_attempt()
    {
        int restores = 0;
        using var portIsBack = new ManualResetEventSlim();
        string? Restore()
        {
            // Stands in for 'adb forward' after the device went away: it fails until the device is
            // back, and only then does the port exist again.
            int attempt = Interlocked.Increment(ref restores);
            if (attempt < 3)
            {
                return "no devices/emulators found";
            }

            if (attempt == 3)
            {
                var listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                _listener = listener;
                portIsBack.Set();
            }

            return null;
        }

        HostSession session = StartSession(configure: options => options with { RestoreTransport = Restore });
        using (FakeClient first = Accept())
        {
            first.Handshake();
            WaitFor(() => session.IsStreaming);
            Assert.Equal(0, Volatile.Read(ref restores));

            // The device goes: the port disappears and the connection with it.
            _listener.Stop();
            first.CloseAbruptly();
        }

        Assert.True(portIsBack.Wait(Patience, TestContext.Current.CancellationToken), "the transport was not set up again");
        Assert.True(Volatile.Read(ref restores) >= 3);
        using FakeClient second = Accept();
        second.Handshake();
        WaitFor(() => session.IsStreaming);

        Assert.Equal((2, 1), (session.Counters.ConnectionsEstablished, session.Counters.ConnectionsLost));
        Assert.True(Logged("no devices/emulators found"), "the reason the transport could not be set up should be logged");
    }

    [Fact]
    public void A_transport_that_cannot_be_set_up_again_fails_the_session_when_its_budget_is_spent()
    {
        int restores = 0;
        HostSession session = StartSession(
            TimeSpan.FromSeconds(1),
            options => options with
            {
                RestoreTransport = () =>
                {
                    Interlocked.Increment(ref restores);
                    return "no devices/emulators found";
                },
            });
        using (FakeClient first = Accept())
        {
            first.Handshake();
            WaitFor(() => session.IsStreaming);
            first.CloseAbruptly();
        }

        WaitFor(() => session.HasEnded);

        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Contains("re-established within 1 s", session.EndReason);
        Assert.Contains("no devices/emulators found", session.EndReason);
        Assert.True(Volatile.Read(ref restores) >= 2, "the transport should have been retried, not tried once");

        // With the transport known to be absent, nothing was connected to.
        Assert.Equal(1, session.Counters.ConnectionsEstablished);
    }

    private static ClientCapabilities Capabilities(int maxWidth, int maxHeight) =>
        new(3200, 2136, 120, [new DecoderCapability("fake.decoder", true, false, maxWidth, maxHeight)]);

    /// <summary>A non-IDR access unit of about <paramref name="size"/> bytes; the payload is filler.</summary>
    private static byte[] LargeNonIdrAccessUnit(int size)
    {
        byte[] filler = new byte[size];
        Array.Fill(filler, (byte)0xAA);
        filler[0] = 0x41;
        return H264Samples.Join(H264Samples.AccessUnitDelimiter, filler);
    }

    /// <summary>Stops the session and returns how many milliseconds that took.</summary>
    private static long TimeToStop(HostSession session, string reason)
    {
        long started = Environment.TickCount64;
        session.Stop(reason);
        return Environment.TickCount64 - started;
    }

    private string LoggedLine(string fragment)
    {
        lock (_log)
        {
            return _log.First(line => line.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private bool Logged(string fragment)
    {
        lock (_log)
        {
            return _log.Any(line => line.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private HostSession StartSession(TimeSpan? reconnectBudget = null, Func<HostSessionOptions, HostSessionOptions>? configure = null)
    {
        var options = new HostSessionOptions(Port, reconnectBudget ?? TimeSpan.FromSeconds(8), SendQueueCapacity: 4, "TEST-HOST", "1.2.3");
        var session = new HostSession(
            configure?.Invoke(options) ?? options,
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

        /// <summary>Ends the connection in an orderly way at the socket level, so that what was sent arrives first.</summary>
        public void CloseAfterSending()
        {
            socket.Shutdown(SocketShutdown.Send);
            socket.Close(1);
        }

        /// <summary>Reads and discards until the host closes or resets the connection; fails if it does not.</summary>
        public void WaitUntilClosedByThePeer()
        {
            byte[] scratch = new byte[4096];
            long deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (!socket.Poll(50_000, SelectMode.SelectRead))
                {
                    continue;
                }

                try
                {
                    if (socket.Receive(scratch) == 0)
                    {
                        return;
                    }
                }
                catch (SocketException)
                {
                    return;
                }
            }

            Assert.Fail($"The host left the connection open for {Patience.TotalSeconds:0} s.");
        }

        public void Dispose() => socket.Dispose();
    }
}
