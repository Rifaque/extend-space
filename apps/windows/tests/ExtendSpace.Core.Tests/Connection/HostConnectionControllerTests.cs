using System.Net.Sockets;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Connection;

/// <summary>
/// The real connection controller against a stand-in client on a real loopback socket: the same
/// connect, handshake, send, receive and close code that would run against the Android app through
/// adb. These are the experiment's <c>HostSessionTests</c> scenarios that belong to one connection,
/// re-pointed at the controller; the ones about retrying belong to the coordinator and are in
/// <c>RealConnectionCoordinatorTests</c>.
/// </summary>
public sealed class HostConnectionControllerTests : IAsyncLifetime
{
    private readonly LoopbackTransport _transport = new();
    private readonly List<string> _log = [];
    private readonly List<IDisposable> _cleanup = [];
    private HostConnectionController _controller = null!;

    public ValueTask InitializeAsync()
    {
        _controller = Create();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.CloseAsync("the test is over", null, CancellationToken.None).Within();
        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

        _transport.Dispose();
    }

    [Fact]
    public async Task Nothing_is_said_on_the_connection_until_capabilities_are_exchanged_and_no_stream_exists_until_it_is_configured()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);
        using TestClient client = Track(await _transport.AcceptAsync());
        Assert.Equal(OutcomeKind.Succeeded, (await open.Within()).Kind);

        // Connected at the socket level, nothing exchanged, nothing to wait for yet.
        Assert.Throws<InvalidOperationException>(() => { _ = _controller.WaitForEndAsync(CancellationToken.None); });
        Assert.Equal(0, client.Socket.Available);

        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(CancellationToken.None);
        await client.SendHelloAsync("Fake Pad");
        Assert.Equal(new HelloMessage(0, 0, "host", "TEST-HOST", "1.2.3"), await client.ExpectAsync<HelloMessage>());
        await client.SendAsync(Loopback.Tablet);

        AttemptOutcome<ClientCapabilities> exchanged = await exchange.Within();
        Assert.Equal(OutcomeKind.Succeeded, exchanged.Kind);
        ClientCapabilities capabilities = exchanged.Value!;
        Assert.Equal(("Fake Pad", "9.9", 3200, 2136, 144.0), (capabilities.DeviceName, capabilities.AppVersion, capabilities.DisplayWidth, capabilities.DisplayHeight, capabilities.RefreshHz));
        Assert.Equal([new DecoderCapability("c2.fake.avc.decoder", true, true, 4096, 4096)], capabilities.Decoders);
        Assert.Throws<InvalidOperationException>(() => { _ = _controller.WaitForEndAsync(CancellationToken.None); });

        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None);
        Assert.Equal(
            new StreamConfigMessage("h264", "high", 1920, 1080, 60, 12_000_000, WireFormat.MaxVideoPayload, "Test Encoder", "hardware"),
            await client.ExpectAsync<StreamConfigMessage>());
        await client.SendAsync(new StreamReadyMessage("c2.fake.avc.decoder", true));

        AttemptOutcome<PeerDescription> configured = await configure.Within();
        Assert.Equal(new PeerDescription("Fake Pad", "9.9", "c2.fake.avc.decoder", true), configured.Value);
        Assert.False(_controller.WaitForEndAsync(CancellationToken.None).IsCompleted);

        // From here the connection keeps itself alive.
        Assert.IsType<PingMessage>(await client.NextMessageAsync(skipPings: false));
        Assert.Equal(1, _controller.Counters.StreamsConfigured);
    }

    [Fact]
    public async Task Video_starts_at_a_keyframe_and_arrives_in_order_with_its_sequence_and_timestamp()
    {
        using TestClient client = await StreamingAsync();

        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 1_000);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 2_000);
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 3_000);
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 4_000);

        var received = new List<(ushort Flags, uint Sequence, long Timestamp, byte[] AccessUnit)>();
        while (received.Count < 3)
        {
            var (header, sequence, timestamp, accessUnit) = await client.NextVideoAsync();
            received.Add((header.Flags, sequence, timestamp, accessUnit));
        }

        // The picture before the keyframe was never sent, and still used up sequence number 0, so
        // that a frame dropped on the host shows up on the client as a gap.
        Assert.Equal([(ushort)3, (ushort)0, (ushort)0], received.Select(f => f.Flags));
        Assert.Equal([1u, 2u, 3u], received.Select(f => f.Sequence));
        Assert.Equal([2_000L, 3_000L, 4_000L], received.Select(f => f.Timestamp));
        Assert.Equal(H264Samples.IdrAccessUnit, received[0].AccessUnit);

        HostConnectionCounters counters = _controller.Counters;
        Assert.Equal((4, 1, 3, 1), (counters.Offered, counters.DroppedAwaitingKeyframe, counters.Transmitted, counters.KeyframesTransmitted));
        Assert.Equal(0, counters.Unaccounted);
    }

    [Fact]
    public async Task Frames_offered_while_no_stream_is_configured_are_counted_and_not_sent_and_ask_for_nothing()
    {
        int wanted = 0;
        _controller.KeyframeWanted += () => Interlocked.Increment(ref wanted);

        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 2);

        Assert.Equal((2, 2, 0), (_controller.Counters.Offered, _controller.Counters.NotSentNoStream, _controller.Counters.Transmitted));
        Assert.Equal(0, wanted);
    }

    [Fact]
    public async Task Pings_are_answered_and_give_the_host_a_clock_relation_which_it_then_shares()
    {
        using TestClient client = await StreamingAsync();

        const long ClientClockAhead = 50_000_000;
        PingMessage? withOffset = null;
        for (int i = 0; i < 20 && withOffset is null; i++)
        {
            if (await client.NextMessageAsync(skipPings: false) is PingMessage ping)
            {
                await client.SendAsync(new PongMessage(ping.Id, ping.HostTimeMicros, MonotonicClock.NowMicros + ClientClockAhead));
                withOffset = ping.ClockOffsetMicros is null ? null : ping;
            }
        }

        Assert.NotNull(withOffset);
        ClockOffset clock = Assert.NotNull(_controller.Clock);

        // Both ends are this process, so the true offset is exactly what the client added.
        Assert.InRange(clock.OffsetMicros, ClientClockAhead - (clock.RoundTripMicros / 2) - 1, ClientClockAhead + (clock.RoundTripMicros / 2) + 1);
    }

    [Fact]
    public async Task A_burst_of_keyframe_requests_is_passed_on_once_and_one_that_comes_too_soon_is_deferred_not_lost()
    {
        using TestClient client = await StreamingAsync();
        var raised = new List<long>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.KeyframeWanted += () =>
        {
            lock (raised)
            {
                raised.Add(Environment.TickCount64);
                if (raised.Count == 2)
                {
                    second.TrySetResult();
                }
            }
        };

        for (int i = 0; i < 5; i++)
        {
            await client.SendAsync(new KeyframeRequestMessage("decode_error"));
        }

        // The first is passed on at once; the other four, within the 500 ms limit, become one more,
        // passed on when the limit allows. Nothing else arrives that could have caused it: on a
        // still screen nothing would (architecture section 5.4).
        await second.Task.Within();
        lock (raised)
        {
            Assert.Equal(2, raised.Count);
            Assert.InRange(raised[1] - raised[0], 400, 2_000);
        }

        Assert.Equal(2, _controller.Counters.KeyframesWanted);
    }

    [Fact]
    public async Task The_keyframe_log_tells_a_request_passed_from_one_deferred_behind_it_and_its_release_and_the_keyframe_that_follows()
    {
        // D62, with D64's rule: a picture refused while the queue waits for a keyframe asks for
        // nothing, because the keyframe it waits for is already owed (here, to the stream's start).
        // Timing here is real, so only the order and content of the lines are checked.
        using TestClient client = await StreamingAsync();

        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 1_000);
        Assert.DoesNotContain(Logged(), line => line.StartsWith("keyframe gate_", StringComparison.Ordinal));
        Assert.Equal(0, _controller.Counters.KeyframesWanted);

        // The client asks: passed on at once, since nothing used the gate's interval.
        await client.SendAsync(new KeyframeRequestMessage("startup"));
        string clientPass = await LoggedEventuallyAsync(line => line.StartsWith("keyframe gate_passed", StringComparison.Ordinal));
        Assert.Contains(" source=client ", clientPass);

        // It asks again within the 500 ms that follows: received, and deferred.
        await client.SendAsync(new KeyframeRequestMessage("sequence_gap"));
        await LoggedEventuallyAsync(line => line.StartsWith("keyframe gate_deferred", StringComparison.Ordinal));
        Assert.Contains(Logged(), line => line.StartsWith("keyframe client_request", StringComparison.Ordinal) && line.EndsWith(" reason=sequence_gap", StringComparison.Ordinal));
        Assert.Contains(" source=client ", Logged().Single(line => line.StartsWith("keyframe gate_deferred", StringComparison.Ordinal)));

        // More refused pictures add nothing to the waiting request.
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 2_000);
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 3_000);

        string released = await LoggedEventuallyAsync(line => line.StartsWith("keyframe gate_released", StringComparison.Ordinal));
        Assert.Contains(" first_source=client client=1 host=0", released);
        Assert.Equal(2, _controller.Counters.KeyframesWanted);

        // Passing requests on is not an encoder's answer: none is claimed until a keyframe is offered.
        Assert.DoesNotContain(Logged(), line => line.StartsWith("keyframe encoder_keyframe", StringComparison.Ordinal) || line.StartsWith("keyframe sent", StringComparison.Ordinal));

        _controller.OfferVideo(H264Samples.IdrAccessUnit, 4_000);
        _ = await client.NextVideoAsync();
        string offered = Assert.Single(Logged(), line => line.StartsWith("keyframe encoder_keyframe", StringComparison.Ordinal));
        Assert.Contains(" raises=2 send=queued", offered);
        await LoggedEventuallyAsync(line => line.StartsWith("keyframe sent", StringComparison.Ordinal));

        // A later keyframe with no request outstanding (a periodic one) adds no line.
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 5_000);
        _ = await client.NextVideoAsync();
        Assert.Single(Logged(), line => line.StartsWith("keyframe encoder_keyframe", StringComparison.Ordinal));
        Assert.Single(Logged(), line => line.StartsWith("keyframe sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D63_an_overflow_asks_for_its_keyframe_only_once_the_writer_can_send_again_and_that_request_is_not_held_by_the_gate()
    {
        // D63, round 1, over a real socket: the client stops reading, the writer blocks, the queue
        // overflows (and keeps overflowing). Before D64 the host asked at the first overflow, at
        // once, and its keyframe was made while nothing could be sent; the next overflow threw that
        // keyframe away, and the keyframe the client needed once reading again was held by the gate
        // for the rest of its 500 ms. Now nothing is asked while the writer is blocked, and the one
        // request made when it is free again is passed on at once.
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        using TestClient client = await StreamingAsync();
        int wanted = 0;
        _controller.KeyframeWanted += () => Interlocked.Increment(ref wanted);

        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 20; i++)
        {
            _controller.OfferVideo(large, 2 + i);
        }

        Assert.True(_controller.Counters.DroppedQueueFull > 0, $"the queue never overflowed: {_controller.Counters}");
        Assert.Equal(0, Volatile.Read(ref wanted));
        Assert.Single(Logged(), line => line.StartsWith("keyframe recovery_owed", StringComparison.Ordinal));
        Assert.DoesNotContain(Logged(), line => line.StartsWith("keyframe gate_", StringComparison.Ordinal));

        // The client reads again. The writer's blocked write completes and it asks, once.
        var keyframes = new List<uint>();
        using var stopReading = new CancellationTokenSource();
        Task reading = Task.Run(async () =>
        {
            try
            {
                while (!stopReading.IsCancellationRequested)
                {
                    (FrameHeader header, byte[] payload) = await client.NextFrameAsync(TimeSpan.FromSeconds(30));
                    if (header.Channel == WireFormat.ChannelVideo && (header.Flags & (ushort)VideoFlags.Keyframe) != 0)
                    {
                        VideoFrameCodec.ReadVideoPayload(payload, out uint sequence, out _);
                        lock (keyframes)
                        {
                            keyframes.Add(sequence);
                        }
                    }
                }
            }
            catch (Exception) when (stopReading.IsCancellationRequested)
            {
            }
        }, TestContext.Current.CancellationToken);

        await WaitForAsync(() => Volatile.Read(ref wanted) == 1);
        string free = await LoggedEventuallyAsync(line => line.StartsWith("keyframe recovery_writer_free", StringComparison.Ordinal));
        Assert.EndsWith(" action=request", free);
        string pass = await LoggedEventuallyAsync(line => line.StartsWith("keyframe gate_passed", StringComparison.Ordinal));
        Assert.Contains(" source=host_queue_full ", pass);

        // The keyframe that answers it is sent.
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 100);
        await WaitForAsync(() =>
        {
            lock (keyframes)
            {
                return keyframes.Count == 2;
            }
        });
        Assert.Equal(1, Volatile.Read(ref wanted));
        stopReading.Cancel();
        client.CloseAbruptly();
        await reading.WaitAsync(Loopback.Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_overflow_answered_by_a_keyframe_before_the_writer_is_free_asks_for_nothing_more()
    {
        // D64: for example the encoder's periodic keyframe arrives while the writer is still blocked.
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        using TestClient client = await StreamingAsync();
        int wanted = 0;
        _controller.KeyframeWanted += () => Interlocked.Increment(ref wanted);

        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 20; i++)
        {
            _controller.OfferVideo(large, 2 + i);
        }

        Assert.True(_controller.Counters.DroppedQueueFull > 0, $"the queue never overflowed: {_controller.Counters}");
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 50);

        Task reading = client.ReadUntilClosedAsync();
        string free = await LoggedEventuallyAsync(line => line.StartsWith("keyframe recovery_writer_free", StringComparison.Ordinal));

        Assert.EndsWith(" action=none_keyframe_queued", free);
        Assert.Equal(0, Volatile.Read(ref wanted));
        Assert.DoesNotContain(Logged(), line => line.StartsWith("keyframe gate_", StringComparison.Ordinal));
        client.CloseAbruptly();

        // Ends either way once the socket is gone; only that it ends matters here.
        await Task.WhenAny(reading, Task.Delay(Loopback.Patience, TestContext.Current.CancellationToken));
        Assert.True(reading.IsCompleted);
    }

    [Fact]
    public async Task An_access_unit_too_large_to_send_leaves_a_gap_and_nothing_after_it_is_sent_until_a_keyframe()
    {
        // D64: the frames after it are predicted from it. Before, they were sent with no gap in
        // their sequence numbers, so the client would have decoded them against a missing picture.
        using TestClient client = await StreamingAsync();
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        _controller.OfferVideo(LargeNonIdrAccessUnit(WireFormat.MaxVideoPayload), 2);
        _controller.OfferVideo(H264Samples.NonIdrAccessUnit, 3);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 4);

        var (first, firstSequence, _, _) = await client.NextVideoAsync();
        var (second, secondSequence, secondTimestamp, _) = await client.NextVideoAsync();

        Assert.Equal(((ushort)3, 0u), (first.Flags, firstSequence));
        Assert.Equal(((ushort)3, 3u, 4L), (second.Flags, secondSequence, secondTimestamp));
        Assert.Equal((1L, 1L), (_controller.Counters.DroppedTooLarge, _controller.Counters.DroppedAwaitingKeyframe));
        await WaitForAsync(() => _controller.Counters.Unaccounted == 0);
    }

    private async Task<string> LoggedEventuallyAsync(Func<string, bool> match)
    {
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (Logged().FirstOrDefault(match) is { } line)
            {
                return line;
            }

            Assert.True(DateTime.UtcNow < giveUp, $"no such line was logged; the log: {string.Join(" | ", Logged())}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_peer_that_accepts_and_closes_as_adb_does_with_no_app_behind_it_is_a_retryable_failure()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);
        TestClient adb = Track(await _transport.AcceptAsync());
        await open.Within();

        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(CancellationToken.None);
        adb.CloseAbruptly();
        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        SessionFault fault = Assert.IsType<SessionFault>(outcome.Fault);
        Assert.True(fault.IsRetryable);
        Assert.Contains(fault.Code, new[] { ConnectionFaultCodes.NoAppListening, ConnectionFaultCodes.ConnectionReset });
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task A_peer_that_is_not_an_extendspace_client_is_a_terminal_protocol_fault_and_is_not_answered()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);
        using TestClient stranger = Track(await _transport.AcceptAsync());
        await open.Within();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(CancellationToken.None);

        await stranger.SendAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();
        Assert.Equal((FaultCategory.Protocol, WireErrorCode.MalformedMessage, FaultTreatment.Terminal), (outcome.Fault!.Category, outcome.Fault.Code, outcome.Fault.Treatment));
        await stranger.WaitUntilClosedByHostAsync();
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task A_client_from_an_incompatible_protocol_version_is_told_so_and_the_fault_is_terminal()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync("Future Pad", min: 1, max: 3);
        await client.ExpectAsync<HelloMessage>();

        ErrorMessage error = await client.ExpectAsync<ErrorMessage>();
        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((WireErrorCode.ProtocolVersionUnsupported, true), (error.Code, error.Fatal));
        Assert.Equal((FaultCategory.Protocol, SessionFaultCodes.ProtocolVersionUnsupported, false), (outcome.Fault!.Category, outcome.Fault.Code, outcome.Fault.IsRetryable));
        Assert.Empty(await client.ReadUntilClosedAsync());
    }

    [Fact]
    public async Task A_peer_that_says_it_is_a_host_is_refused_as_an_unexpected_message()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync("Another PC", role: "host");

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((WireErrorCode.UnexpectedMessage, false), (outcome.Fault!.Code, outcome.Fault.IsRetryable));
    }

    [Theory]
    [InlineData("""{"display":{"width":0,"height":1080},"decoders":[]}""")]
    [InlineData("""{"display":{"width":1920,"height":1080,"refresh_hz":-1},"decoders":[]}""")]
    [InlineData("""{"display":{"width":1920,"height":1080}}""")]
    [InlineData("""{"display":{"width":1920,"height":1080},"decoders":[{"name":"x","hardware":true,"max_width":-4096,"max_height":4096}]}""")]
    [InlineData("""{"display":{"width":1920,"height":1080},"decoders":[{"name":"x","hardware":"yes","max_width":4096,"max_height":4096}]}""")]
    [InlineData("""{"display":{"width":1920,"height":1080},"decoders":[{"name":"x","hardware":true,"low_latency":1,"max_width":4096,"max_height":4096}]}""")]
    [InlineData("""{"display":"1920x1080","decoders":[]}""")]
    public async Task Capabilities_that_are_malformed_or_impossible_are_a_terminal_fault_and_the_client_is_told(string json)
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();

        await client.SendAsync(RawControl(ControlType.Capabilities, json));

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();
        Assert.Equal((FaultCategory.Protocol, WireErrorCode.MalformedMessage, false), (outcome.Fault!.Category, outcome.Fault.Code, outcome.Fault.IsRetryable));
        Assert.Equal(WireErrorCode.MalformedMessage, (await client.ExpectAsync<ErrorMessage>()).Code);
    }

    [Fact]
    public async Task A_client_with_no_decoder_large_enough_is_told_through_the_close_that_the_coordinator_decides_on()
    {
        // The controller does not choose the stream; the coordinator does, and ends the session
        // through CloseAsync with the fault, which the client is told.
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync("Small Pad");
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.TinyDecoder);
        Assert.Equal(OutcomeKind.Succeeded, (await exchange.Within()).Kind);

        var fault = new SessionFault(FaultCategory.Protocol, SessionFaultCodes.NoCommonConfiguration, "No decoder fits.", FaultTreatment.Terminal);
        Task close = _controller.CloseAsync("no decoder fits", fault, CancellationToken.None);

        ErrorMessage error = await client.ExpectAsync<ErrorMessage>();
        Assert.Equal((WireErrorCode.NoCommonConfiguration, "No decoder fits.", true), (error.Code, error.Message, error.Fatal));
        client.CloseAfterSending();
        await close.Within();
        Assert.Equal(1, _transport.Releases);
    }

    [Fact]
    public async Task A_client_that_rejects_the_stream_is_a_terminal_negotiation_fault()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.Tablet);
        await exchange.Within();

        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None);
        await client.ExpectAsync<StreamConfigMessage>();
        await client.SendAsync(new ErrorMessage(WireErrorCode.StreamConfigRejected, "no decoder could be created", true));

        AttemptOutcome<PeerDescription> outcome = await configure.Within();
        Assert.Equal((FaultCategory.Protocol, SessionFaultCodes.StreamConfigRejected, false), (outcome.Fault!.Category, outcome.Fault.Code, outcome.Fault.IsRetryable));
        Assert.Equal(SessionErrorCode.NegotiationFailed, SessionErrors.From(outcome.Fault, hasStreamed: false).Code);
        Assert.False(_controller.HasConnection);
    }

    [Theory]
    [InlineData(WireErrorCode.Busy, FaultCategory.Connection, true)]
    [InlineData(WireErrorCode.Timeout, FaultCategory.Timeout, true)]
    [InlineData(WireErrorCode.InternalError, FaultCategory.Protocol, false)]
    [InlineData("some_future_code", FaultCategory.Protocol, false)]
    public async Task An_error_from_the_client_during_the_handshake_is_classified_by_its_code(string code, FaultCategory category, bool retryable)
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(new ErrorMessage(code, "said by the client", true));

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((category, retryable), (outcome.Fault!.Category, outcome.Fault.IsRetryable));
    }

    [Fact]
    public async Task A_client_that_says_goodbye_instead_of_its_hello_has_ended_the_session_which_is_not_a_fault()
    {
        // Protocol sections 7 and 14: bye is the orderly end of a session, from either side, at any
        // point after the preface; its receiver stops at once, replies nothing and does not
        // reconnect. Decision D44: reported as PeerEnded, never as a fault.
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendAsync([.. WireFormat.Preface, .. ControlCodec.Encode(new ByeMessage("the user stopped waiting"))]);

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((OutcomeKind.PeerEnded, "the user stopped waiting", null), (outcome.Kind, outcome.PeerReason, outcome.Fault));
        await client.ExpectAsync<HelloMessage>();
        Assert.Empty(await client.ReadUntilClosedAsync());
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task A_client_that_says_goodbye_instead_of_its_capabilities_has_ended_the_session_which_is_not_a_fault()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(new ByeMessage("the user stopped waiting"));

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((OutcomeKind.PeerEnded, "the user stopped waiting", null), (outcome.Kind, outcome.PeerReason, outcome.Fault));
        Assert.Empty(await client.ReadUntilClosedAsync());
        Assert.False(_controller.HasConnection);
        Assert.Equal(0, _transport.Releases);
    }

    [Fact]
    public async Task A_client_that_says_goodbye_instead_of_being_ready_has_ended_the_session_which_is_not_a_fault()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.Tablet);
        await exchange.Within();
        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None);
        await client.ExpectAsync<StreamConfigMessage>();

        await client.SendAsync(new ByeMessage("the screen was closed"));

        AttemptOutcome<PeerDescription> outcome = await configure.Within();
        Assert.Equal((OutcomeKind.PeerEnded, "the screen was closed"), (outcome.Kind, outcome.PeerReason));
        Assert.Empty(await client.ReadUntilClosedAsync());
        Assert.Throws<InvalidOperationException>(() => { _ = _controller.WaitForEndAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task A_client_that_accepts_and_never_speaks_is_a_retryable_timeout_explained_and_is_told_and_closed()
    {
        Recreate(options => options with { HandshakeStepTimeout = TimeSpan.FromMilliseconds(300) });
        (TestClient busy, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.Equal((FaultCategory.Timeout, ConnectionFaultCodes.HandshakeTimeout, true), (outcome.Fault!.Category, outcome.Fault.Code, outcome.Fault.IsRetryable));
        Assert.Contains("serving another PC", outcome.Fault.Message);

        // Protocol section 7: the side that waited sends a fatal timeout error, then closes.
        await busy.ExpectAsync<HelloMessage>();
        ErrorMessage error = await busy.ExpectAsync<ErrorMessage>();
        Assert.Equal((WireErrorCode.Timeout, true), (error.Code, error.Fatal));
        Assert.Empty(await busy.ReadUntilClosedAsync());
    }

    [Fact]
    public async Task A_handshake_that_stalls_is_given_up_its_socket_closed_and_the_next_client_served()
    {
        Recreate(options => options with { HandshakeStepTimeout = TimeSpan.FromMilliseconds(300) });
        (TestClient stalled, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await stalled.SendHelloAsync("Stalled Pad");

        // No capabilities follow. The controller must not keep this half-made connection.
        Assert.Equal(ConnectionFaultCodes.HandshakeTimeout, (await exchange.Within()).Fault!.Code);
        await stalled.ReadUntilClosedAsync();
        Assert.False(_controller.HasConnection);

        using TestClient next = await StreamingAsync();
        Assert.Equal(1, _controller.Counters.StreamsConfigured);
    }

    [Fact]
    public async Task A_client_that_leaves_partway_through_the_handshake_is_a_retryable_failure()
    {
        (TestClient leaver, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        await leaver.SendHelloAsync("Leaving Pad");
        await leaver.ExpectAsync<HelloMessage>();
        leaver.CloseAbruptly();

        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

        Assert.True(outcome.Fault!.IsRetryable);
        Assert.Equal(FaultCategory.Connection, outcome.Fault.Category);
    }

    [Fact]
    public async Task A_client_that_says_goodbye_while_streaming_ends_the_connection_as_peer_ended_and_is_closed()
    {
        using TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        await client.SendAsync(new ByeMessage("the user closed the screen"));

        ConnectionEnd end = await ended.Within();
        Assert.Equal((ConnectionEndKind.PeerEnded, "the user closed the screen"), (end.Kind, end.PeerReason));
        Assert.Empty(await client.ReadUntilClosedAsync());
    }

    [Fact]
    public async Task A_connection_that_ends_in_the_middle_of_a_message_is_lost_retryably_and_said_to_be_so()
    {
        using TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        byte[] stats = ControlCodec.Encode(new StatsMessage { FramesReceived = 3, FramesDecoded = 3, FramesRendered = 3 });
        await client.SendAsync(stats[..(stats.Length / 2)]);
        client.CloseAfterSending();

        ConnectionEnd end = await ended.Within();
        Assert.Equal((ConnectionEndKind.Lost, ConnectionFaultCodes.ConnectionClosed, true), (end.Kind, end.Fault!.Code, end.Fault.IsRetryable));
        Assert.Contains("in the middle of a message", end.Fault.Message);
    }

    [Fact]
    public async Task An_oversized_frame_from_the_client_ends_the_connection_as_fatal_and_the_client_is_told()
    {
        using TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        byte[] header = new byte[WireFormat.HeaderLength];
        WireFormat.WriteHeader(header, new FrameHeader(WireFormat.ChannelControl, (byte)ControlType.Stats, 0, 0xFFFFFFFF));
        await client.SendAsync(header);

        ConnectionEnd end = await ended.Within();
        Assert.Equal((ConnectionEndKind.Fatal, WireErrorCode.MessageTooLarge, false), (end.Kind, end.Fault!.Code, end.Fault.IsRetryable));
        Assert.Contains(await client.ReadUntilClosedAsync(), message => message is ErrorMessage { Code: WireErrorCode.MessageTooLarge, Fatal: true });
    }

    [Fact]
    public async Task A_client_that_keeps_talking_is_not_timed_out_however_long_the_connection_lasts()
    {
        Recreate(options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(500) });
        using TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        // Four timeouts' worth of time, with something arriving well inside each. The pauses are
        // the stimulus, not a way of waiting for the controller.
        for (int i = 0; i < 20; i++)
        {
            await client.SendAsync(new StatsMessage { FramesReceived = i, FramesDecoded = i, FramesRendered = i });
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.False(ended.IsCompleted, "a client that is sending must not be treated as silent");
    }

    [Fact]
    public async Task A_client_that_goes_silent_is_lost_after_the_keepalive_timeout_and_its_socket_closed()
    {
        Recreate(options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(500) });
        using TestClient silent = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        ConnectionEnd end = await ended.Within();

        Assert.Equal((ConnectionEndKind.Lost, ConnectionFaultCodes.KeepaliveTimeout, true), (end.Kind, end.Fault!.Code, end.Fault.IsRetryable));
        Assert.Contains("0.5 s", end.Fault.Message);
        await silent.ReadUntilClosedAsync();
    }

    [Fact]
    public async Task A_silent_client_that_has_also_stopped_reading_is_still_lost_and_its_socket_closed()
    {
        // What a frozen app looks like: nothing comes back and nothing is taken. The writer ends up
        // blocked, and the connection must end all the same.
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        Recreate(options => options with { KeepaliveTimeout = TimeSpan.FromMilliseconds(600) });
        using TestClient frozen = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 8; i++)
        {
            _controller.OfferVideo(large, 2 + i);
        }

        ConnectionEnd end = await ended.Within();

        // The wait for the client and the blocked write run out together; either is the silence.
        Assert.Equal(ConnectionEndKind.Lost, end.Kind);
        Assert.Contains(end.Fault!.Code, new[] { ConnectionFaultCodes.KeepaliveTimeout, ConnectionFaultCodes.SendTimeout });
        await frozen.WaitUntilClosedByHostAsync();
        await WaitForAsync(() => _controller.Counters.Unaccounted == 0);
    }

    [Fact]
    public async Task A_client_that_stops_reading_costs_frames_not_memory_and_does_not_hold_up_closing()
    {
        // Small receive buffers, so that the host's writes block after very little.
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        using TestClient client = await StreamingAsync();

        byte[] large = LargeNonIdrAccessUnit(1024 * 1024);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        for (int i = 0; i < 40; i++)
        {
            _controller.OfferVideo(large, 2 + i);
        }

        HostConnectionCounters blocked = _controller.Counters;
        Assert.InRange(blocked.Unaccounted, 0, 5);
        Assert.True(blocked.DroppedQueueFull + blocked.DroppedAwaitingKeyframe >= 30, $"only {blocked.DroppedQueueFull + blocked.DroppedAwaitingKeyframe} of 41 frames were dropped");

        long took = await Loopback.TimeAsync(() => _controller.CloseAsync("stopped while the socket was full", null, CancellationToken.None).Within());

        // 250 ms for the writer to let go, then the socket is closed, which ends the blocked write.
        Assert.True(took < 1_500, $"closing took {took} ms with a write blocked");
        Assert.Equal(0, _controller.Counters.Unaccounted);
        Assert.Equal(41, _controller.Counters.Offered);

        // No goodbye could reach a client that reads nothing. Which write was the blocked one
        // depends on how much the loopback stack had buffered: either the writer was still inside a
        // frame (and the goodbye was not attempted), or it had just finished one and the goodbye
        // itself could not be written within its bound. Both are bounded, and both are reported.
        Assert.Contains(Logged(), line => line.Contains("without a final message", StringComparison.Ordinal)
            || line.Contains("could not send the final Bye", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_a_stream_says_goodbye_with_the_reason_waits_for_the_client_to_close_and_releases_the_transport()
    {
        using TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);

        Task close = _controller.CloseAsync("the session was stopped on the PC", null, CancellationToken.None);

        ByeMessage bye = await client.ExpectAsync<ByeMessage>();
        Assert.Equal("the session was stopped on the PC", bye.Reason);
        client.CloseAfterSending();
        await close.Within();

        Assert.Equal(ConnectionEndKind.Closed, (await ended.Within()).Kind);
        Assert.Equal(1, _transport.Releases);
        Assert.False(_controller.HasConnection);
    }

    [Theory]
    [InlineData(FaultCategory.Protocol, SessionFaultCodes.NoCommonConfiguration, WireErrorCode.NoCommonConfiguration)]
    [InlineData(FaultCategory.Protocol, "some_host_specific_code", WireErrorCode.InternalError)]
    [InlineData(FaultCategory.Capture, "monitor_resized", WireErrorCode.InternalError)]
    [InlineData(FaultCategory.Encoder, "encoder_unavailable", WireErrorCode.InternalError)]
    [InlineData(FaultCategory.Timeout, "keepalive_timeout", WireErrorCode.Timeout)]
    public void A_fault_is_told_to_the_client_as_the_protocol_error_it_amounts_to(FaultCategory category, string code, string wire)
    {
        Assert.Equal(wire, HostConnectionController.WireCode(new SessionFault(category, code, "x", FaultTreatment.Terminal)));
    }

    [Fact]
    public async Task Closing_is_safe_in_any_state_and_more_than_once()
    {
        await _controller.CloseAsync("nothing was ever opened", null, CancellationToken.None).Within();
        await _controller.CloseAsync("and again", null, CancellationToken.None).Within();

        using TestClient client = await StreamingAsync();
        Task first = _controller.CloseAsync("once", null, CancellationToken.None);
        Task second = _controller.CloseAsync("twice", null, CancellationToken.None);
        client.CloseAfterSending();
        await Task.WhenAll(first, second).Within();

        Assert.Equal(4, _transport.Releases);
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task Opening_again_discards_the_connection_left_from_the_last_attempt()
    {
        using TestClient first = await StreamingAsync();

        Task<AttemptOutcome> reopen = _controller.OpenAsync(CancellationToken.None);
        using TestClient second = Track(await _transport.AcceptAsync());
        Assert.Equal(OutcomeKind.Succeeded, (await reopen.Within()).Kind);

        // The first connection was closed without a word: it is not the session that is ending.
        Assert.Empty(await first.ReadUntilClosedAsync());
        Assert.Equal(2, _transport.Prepares);
        Assert.Equal(0, _transport.Releases);
    }

    [Fact]
    public async Task A_transport_that_cannot_be_set_up_fails_the_open_with_its_own_fault_and_nothing_is_connected()
    {
        var absent = new SessionFault(FaultCategory.Transport, TransportFaultCodes.DeviceNotAttached, "No device.", FaultTreatment.Retryable);
        _transport.Prepare = (_, _) => Task.FromResult(AttemptOutcome<TransportEndpoint>.Failed(absent));

        AttemptOutcome outcome = await _controller.OpenAsync(CancellationToken.None).Within();

        Assert.Same(absent, outcome.Fault);
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task Nothing_listening_on_the_port_is_a_retryable_failure_within_the_connect_bound()
    {
        Recreate(options => options with { ConnectTimeout = TimeSpan.FromMilliseconds(500) });
        _transport.Listener.Stop();

        AttemptOutcome outcome = await _controller.OpenAsync(CancellationToken.None).Within(TimeSpan.FromSeconds(3));

        Assert.True(outcome.Fault!.IsRetryable);
        Assert.Contains(outcome.Fault.Code, new[] { ConnectionFaultCodes.ConnectionRefused, ConnectionFaultCodes.ConnectTimeout });
    }

    [Fact]
    public async Task The_steps_of_an_attempt_must_be_called_in_order()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ExchangeCapabilitiesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => { _ = _controller.WaitForEndAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task Cancelling_the_wait_for_the_end_ends_only_the_wait()
    {
        using TestClient client = await StreamingAsync();
        using var cancel = new CancellationTokenSource();
        Task<ConnectionEnd> waiting = _controller.WaitForEndAsync(cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Within());
        Assert.True(_controller.HasConnection);

        // Still alive: it keeps pinging.
        Assert.IsType<PingMessage>(await client.NextMessageAsync(skipPings: false));
        Assert.False(_controller.WaitForEndAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task What_the_client_sent_is_never_logged_in_full_or_with_control_characters()
    {
        string printable = HostConnectionController.Printable(new string('x', 500) + "\r\n\u0007");
        Assert.True(printable.Length <= 161, $"{printable.Length} characters");
        Assert.DoesNotContain(printable, char.IsControl);
        await Task.CompletedTask;
    }

    // ---- Helpers ----

    internal static byte[] LargeNonIdrAccessUnit(int size)
    {
        byte[] filler = new byte[size];
        Array.Fill(filler, (byte)0xAA);
        filler[0] = 0x41;
        return H264Samples.Join(H264Samples.AccessUnitDelimiter, filler);
    }

    internal static byte[] RawControl(ControlType type, string json)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[WireFormat.HeaderLength + payload.Length];
        WireFormat.WriteHeader(frame, new FrameHeader(WireFormat.ChannelControl, (byte)type, 0, (uint)payload.Length));
        payload.CopyTo(frame, WireFormat.HeaderLength);
        return frame;
    }

    private HostConnectionController Create(Func<HostConnectionOptions, HostConnectionOptions>? configure = null)
    {
        var options = new HostConnectionOptions
        {
            DeviceName = "TEST-HOST",
            AppVersion = "1.2.3",
            Log = line =>
            {
                lock (_log)
                {
                    _log.Add(line);
                }
            },
        };
        return new HostConnectionController(_transport, configure?.Invoke(options) ?? options);
    }

    private void Recreate(Func<HostConnectionOptions, HostConnectionOptions> configure) => _controller = Create(configure);

    private string[] Logged()
    {
        lock (_log)
        {
            return [.. _log];
        }
    }

    private TestClient Track(TestClient client)
    {
        _cleanup.Add(client);
        return client;
    }

    private async Task<(TestClient Client, Task<AttemptOutcome<ClientCapabilities>> Exchange)> OpenAndExchangeAsync()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);
        TestClient client = Track(await _transport.AcceptAsync());
        Assert.Equal(OutcomeKind.Succeeded, (await open.Within()).Kind);
        return (client, _controller.ExchangeCapabilitiesAsync(CancellationToken.None));
    }

    private async Task<TestClient> StreamingAsync()
    {
        (TestClient client, Task<AttemptOutcome<ClientCapabilities>> exchange) = await OpenAndExchangeAsync();
        Task handshake = client.HandshakeAsync();
        Assert.Equal(OutcomeKind.Succeeded, (await exchange.Within()).Kind);
        Assert.Equal(OutcomeKind.Succeeded, (await _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None).Within()).Kind);
        await handshake.Within();
        return client;
    }

    /// <summary>Polls a counter that has no event, within the failsafe.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)Loopback.Patience.TotalMilliseconds;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "the expected state was not reached");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
