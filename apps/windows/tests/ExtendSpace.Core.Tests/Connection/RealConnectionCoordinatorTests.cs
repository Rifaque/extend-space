using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Tests.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Connection;

/// <summary>
/// The production <see cref="SessionCoordinator"/> driving the real <see cref="HostConnectionController"/>
/// over a loopback socket to a stand-in client, with the scripted pipeline from the coordinator's own
/// tests in place of capture and encoding (stage 4). These are the experiment's <c>HostSessionTests</c>
/// scenarios about retrying, stopping and ending, re-pointed at the pair that replaces
/// <c>HostSession</c>. Time is real here, so budgets are shortened; the coordinator's policy is
/// covered exactly, with a manual clock, by <see cref="SessionCoordinatorTests"/>.
/// </summary>
public sealed class RealConnectionCoordinatorTests : IAsyncLifetime
{
    private readonly LoopbackTransport _transport = new();
    private readonly CallLog _log = new();
    private readonly List<IDisposable> _cleanup = [];
    private readonly Lock _statesGate = new();
    private readonly List<SessionState> _states = [];
    private FakePipeline _pipeline = null!;
    private HostConnectionController _connection = null!;
    private SessionCoordinator _coordinator = null!;

    public ValueTask InitializeAsync()
    {
        Compose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator.Stop())
        {
            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        }

        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

        _transport.Dispose();
        Assert.Equal(0, _coordinator.UnhandledInputFailures);
    }

    [Fact]
    public async Task A_session_negotiates_with_the_client_reaches_streaming_and_asks_for_the_first_keyframe()
    {
        Assert.Equal(SessionStartResult.Started, Start());
        using TestClient client = await AcceptAsync();
        (HelloMessage hello, StreamConfigMessage config) = await client.HandshakeAsync();

        await WaitForStreamingAsync(keyframes: 1);

        Assert.Equal("host", hello.Role);

        // The coordinator chose 1080p60 from the client's decoder (D16) and the pipeline prepared it.
        Assert.Equal((1920, 1080, 60, 12_000_000, "hardware encoder", "hardware"), (config.Width, config.Height, config.FrameRate, config.Bitrate, config.Encoder, config.EncoderAcceleration));
        Assert.Equal(
            ["pipeline.Acquire", "pipeline.WatchFault", "pipeline.Prepare 1920x1080@60", "pipeline.Start", "pipeline.Encoding on", "pipeline.Keyframe"],
            _log.Entries.Where(entry => entry.StartsWith("pipeline.", StringComparison.Ordinal)));
        Assert.Equal(1, _pipeline.KeyframeRequests);
        Assert.Equal([SessionPhase.Connecting, SessionPhase.Negotiating, SessionPhase.Streaming], Phases());

        // Video from the pipeline's thread reaches the client through the connection.
        _pipeline.Sink!(H264Samples.IdrAccessUnit, 5_000);
        var (header, sequence, timestamp, _) = await client.NextVideoAsync();
        Assert.Equal(((ushort)3, 0u, 5_000L), (header.Flags, sequence, timestamp));
    }

    [Fact]
    public async Task Stopping_says_goodbye_tears_down_in_order_releases_the_transport_and_ends_disconnected()
    {
        using TestClient client = await StreamingAsync();

        Assert.True(_coordinator.Stop());

        ByeMessage bye = await client.ExpectAsync<ByeMessage>();
        Assert.Equal("the session was stopped on the PC", bye.Reason);
        client.CloseAfterSending();
        await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);

        Assert.True(_log.IndexOf("pipeline.Stop") >= 0);
        Assert.Equal(1, _transport.Releases);
        Assert.False(_connection.HasConnection);
        Assert.True(_coordinator.IsReleased);
        Assert.Empty(_coordinator.LastTeardownFaults);
    }

    [Fact]
    public async Task A_client_that_says_goodbye_ends_the_session_without_error_or_a_reconnection_attempt()
    {
        using TestClient client = await StreamingAsync();

        await client.SendAsync(new ByeMessage("the user closed the screen"));

        SessionState end = await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        Assert.Null(end.Error);
        Assert.Equal(1, _transport.Prepares);
        Assert.DoesNotContain(SessionPhase.Reconnecting, Phases());
    }

    [Fact]
    public async Task A_client_that_says_goodbye_during_the_handshake_ends_the_session_as_disconnected_without_a_retry()
    {
        Start();
        using TestClient client = await AcceptAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(new ByeMessage("the user stopped waiting"));

        SessionState end = await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);

        Assert.Null(end.Error);
        Assert.DoesNotContain(SessionPhase.Failed, Phases());
        Assert.Equal((1, 1), (_transport.Prepares, _transport.Releases));
        Assert.Empty(await client.ReadUntilClosedAsync());
        Assert.True(_coordinator.IsReleased);
    }

    [Fact]
    public async Task A_lost_connection_is_re_established_after_a_delay_with_the_transport_restored_and_a_keyframe_asked_for_again()
    {
        using (TestClient first = await StreamingAsync())
        {
            first.CloseAbruptly();
        }

        SessionState reconnecting = await WaitForAsync(state => state.Phase == SessionPhase.Reconnecting);
        Assert.True(reconnecting.LastAttemptFault is { IsRetryable: true, Category: FaultCategory.Connection }, $"{reconnecting.LastAttemptFault}");
        Assert.Contains("pipeline.Encoding off", _log.Entries);

        using TestClient second = await AcceptAsync();
        await second.HandshakeAsync();
        SessionState streaming = await WaitForAsync(state => state.Phase == SessionPhase.Streaming && state.HasStreamed && _pipeline.KeyframeRequests == 2, pollCounters: true);

        // The loss's fault is kept until negotiation completes, then cleared (D22, D32).
        Assert.Null(streaming.LastAttemptFault);

        // Source and encoder kept; the forward set up again before the new attempt (D7).
        Assert.Equal((1, 1, 1), (_pipeline.Acquires, _pipeline.Prepares, _pipeline.Starts));
        Assert.Equal(2, _transport.Prepares);
        Assert.Equal(0, _transport.Releases);
        Assert.Equal(2, _connection.Counters.StreamsConfigured);
    }

    [Fact]
    public async Task A_negotiation_that_times_out_is_retried_and_the_next_client_is_served()
    {
        Compose(connection: options => options with { HandshakeStepTimeout = TimeSpan.FromMilliseconds(300) });
        Start();
        using (TestClient silent = await AcceptAsync())
        {
            SessionState failedAttempt = await WaitForAsync(state => state.LastAttemptFault?.Code == ConnectionFaultCodes.HandshakeTimeout);
            Assert.Equal(SessionPhase.Connecting, failedAttempt.Phase);
            await silent.ReadUntilClosedAsync();
        }

        using TestClient client = await AcceptAsync();
        await client.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming);
        Assert.Equal(1, _pipeline.Prepares);
    }

    [Fact]
    public async Task A_peer_that_accepts_and_closes_as_adb_does_with_no_app_is_retried_until_an_app_answers()
    {
        Start();
        for (int i = 0; i < 3; i++)
        {
            using TestClient adb = await AcceptAsync();
            adb.CloseAbruptly();
        }

        using TestClient real = await AcceptAsync();
        await real.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming);
        Assert.True(_transport.Prepares >= 4);
    }

    [Fact]
    public async Task A_client_from_an_incompatible_protocol_version_fails_the_session_at_once_without_a_retry()
    {
        Start();
        using TestClient client = await AcceptAsync();
        await client.SendHelloAsync("Future Pad", min: 2, max: 2);

        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);

        Assert.Equal(SessionErrorCode.IncompatibleProtocol, failed.Error!.Code);
        Assert.Equal(SessionFaultCodes.ProtocolVersionUnsupported, failed.Error.Cause!.Code);
        Assert.Equal(1, _transport.Prepares);
        Assert.Equal(1, _transport.Releases);
        Assert.Contains(await client.ReadUntilClosedAsync(), message => message is ErrorMessage { Code: WireErrorCode.ProtocolVersionUnsupported });
    }

    [Fact]
    public async Task A_client_with_no_decoder_large_enough_is_told_and_the_session_fails_without_a_retry()
    {
        Start();
        using TestClient client = await AcceptAsync();
        await client.SendHelloAsync("Small Pad");
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.TinyDecoder);

        ErrorMessage error = await client.ExpectAsync<ErrorMessage>();
        client.CloseAfterSending();
        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);

        Assert.Equal((WireErrorCode.NoCommonConfiguration, true), (error.Code, error.Fatal));
        Assert.Equal(SessionErrorCode.NegotiationFailed, failed.Error!.Code);
        Assert.Equal(0, _pipeline.Prepares);
        Assert.Equal(1, _transport.Prepares);
    }

    [Fact]
    public async Task A_client_that_rejects_the_stream_fails_the_session_as_a_negotiation_failure()
    {
        Start();
        using TestClient client = await AcceptAsync();
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.Tablet);
        await client.ExpectAsync<StreamConfigMessage>();
        await client.SendAsync(new ErrorMessage(WireErrorCode.StreamConfigRejected, "the decoder could not be created", true));

        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);

        Assert.Equal(SessionErrorCode.NegotiationFailed, failed.Error!.Code);
        Assert.Equal(1, _transport.Prepares);
    }

    [Fact]
    public async Task A_transport_that_cannot_be_set_up_fails_the_session_once_its_budget_is_spent()
    {
        Compose(coordinator: options => options with { RetryBudget = TimeSpan.FromSeconds(1) });
        _transport.Prepare = (_, _) => Task.FromResult(AttemptOutcome<TransportEndpoint>.Failed(
            new SessionFault(FaultCategory.Transport, TransportFaultCodes.DeviceNotAttached, "No Android device is attached.", FaultTreatment.Retryable)));

        Start();
        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);

        Assert.Equal(SessionErrorCode.TransportUnavailable, failed.Error!.Code);
        Assert.Equal(TransportFaultCodes.DeviceNotAttached, failed.Error.Cause!.Code);
        Assert.Contains("within 1 s", failed.Error.Detail);
        Assert.True(_transport.Prepares >= 3, $"{_transport.Prepares} attempts");
        Assert.Equal(1, _transport.Releases);
    }

    [Fact]
    public async Task A_terminal_transport_fault_fails_the_session_at_once()
    {
        _transport.Prepare = (_, _) => Task.FromResult(AttemptOutcome<TransportEndpoint>.Failed(
            new SessionFault(FaultCategory.Transport, TransportFaultCodes.AdbNotFound, "adb was not found.", FaultTreatment.Terminal, UserActionable: true)));

        Start();
        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);

        Assert.Equal((SessionErrorCode.TransportUnavailable, "adb was not found."), (failed.Error!.Code, failed.Error.Detail));
        Assert.Equal(1, _transport.Prepares);
    }

    [Fact]
    public async Task After_a_loss_the_transport_is_set_up_again_before_each_attempt_until_the_device_returns()
    {
        using (TestClient first = await StreamingAsync())
        {
            // The device goes: the transport cannot be set up for two attempts, then it can.
            _transport.Prepare = (attempt, _) => Task.FromResult(attempt <= 3
                ? AttemptOutcome<TransportEndpoint>.Failed(new SessionFault(FaultCategory.Transport, TransportFaultCodes.DeviceNotAttached, "No device.", FaultTreatment.Retryable))
                : AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(_transport.EndPoint)));
            first.CloseAbruptly();
        }

        using TestClient second = await AcceptAsync();
        await second.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming && _pipeline.KeyframeRequests == 2, pollCounters: true);

        Assert.Equal(4, _transport.Prepares);
        Assert.Contains(_states_Faults(), fault => fault.Code == TransportFaultCodes.DeviceNotAttached);
    }

    [Fact]
    public async Task Stopping_during_negotiation_ends_promptly_without_waiting_for_the_handshake_timeout()
    {
        Start();
        using TestClient silent = await AcceptAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Negotiating);
        await silent.ExpectAsync<HelloMessage>();

        long took = await Loopback.TimeAsync(async () =>
        {
            Assert.True(_coordinator.Stop());
            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        });

        Assert.True(took < 1_500, $"stopping took {took} ms; the handshake step allows 5 s");
        Assert.DoesNotContain(_states_Faults(), _ => true);
        Assert.Equal(1, _transport.Releases);
        Assert.True(_coordinator.IsReleased);
        await silent.ReadUntilClosedAsync();
    }

    [Fact]
    public async Task Stopping_while_a_write_is_blocked_on_a_client_that_stopped_reading_is_bounded_and_releases_everything()
    {
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        using TestClient client = await StreamingAsync();
        byte[] large = HostConnectionControllerTests.LargeNonIdrAccessUnit(2 * 1024 * 1024);
        _pipeline.Sink!(H264Samples.IdrAccessUnit, 1);
        _pipeline.Sink!(large, 2);
        _pipeline.Sink!(large, 3);

        long took = await Loopback.TimeAsync(async () =>
        {
            Assert.True(_coordinator.Stop());
            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        });

        Assert.True(took < 2_000, $"stopping took {took} ms with a write blocked");
        Assert.True(_coordinator.IsReleased);
        Assert.Empty(_coordinator.LastTeardownFaults);
        Assert.False(_connection.HasConnection);
        Assert.Equal(0, _connection.Counters.Unaccounted);
    }

    [Fact]
    public async Task A_second_start_while_a_session_is_running_is_refused_and_connects_nothing()
    {
        using TestClient client = await StreamingAsync();

        Assert.Equal(SessionStartResult.AlreadyActive, Start());
        Assert.Equal(1, _transport.Prepares);
    }

    [Fact]
    public async Task A_keyframe_the_client_asks_for_reaches_the_pipeline()
    {
        using TestClient client = await StreamingAsync();
        int before = _pipeline.KeyframeRequests;

        await client.SendAsync(new KeyframeRequestMessage("surface_recreated"));

        await WaitForAsync(_ => _pipeline.KeyframeRequests == before + 1, pollCounters: true);
    }

    [Fact]
    public async Task Starts_and_stops_racing_from_other_threads_always_end_disconnected_with_everything_released()
    {
        // A client that serves every connection that reaches it, as the app would.
        using var serving = new CancellationTokenSource();
        Task server = ServeEveryConnectionAsync(serving.Token);

        for (int round = 0; round < 25; round++)
        {
            Assert.Equal(SessionStartResult.Started, Start());
            int delay = round % 5;
            await Task.Run(async () =>
            {
                await Task.Delay(delay, TestContext.Current.CancellationToken);
                _coordinator.Stop();
            }, TestContext.Current.CancellationToken);

            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
            Assert.True(_coordinator.IsReleased, $"round {round}: not released");
            Assert.False(_connection.HasConnection, $"round {round}: a connection was kept");
            Assert.Equal(round + 1, _transport.Releases);
        }

        Assert.DoesNotContain(SessionPhase.Failed, Phases());
        await serving.CancelAsync();
        await server.Within();
    }

    [Fact]
    public async Task Removing_the_forward_cannot_hold_the_session_past_the_deadline_and_a_new_session_waits_for_it()
    {
        // The real controller, with the adb removal blocked: the connection is closed at once, the
        // session is terminal at the deadline, and nothing new starts until the forward is gone.
        Compose(coordinator: options => options with { TeardownDeadline = TimeSpan.FromMilliseconds(300) });
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.Release = _ => removed.Task;
        using TestClient client = await StreamingAsync();

        long took = await Loopback.TimeAsync(async () =>
        {
            Assert.True(_coordinator.Stop());
            client.CloseAfterSending();
            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        });

        Assert.True(took < 1_500, $"the session took {took} ms to end with a 300 ms deadline");
        Assert.Contains(_coordinator.LastTeardownFaults, fault => fault.Code == SessionFaultCodes.TeardownStepOverran);
        Assert.False(_connection.HasConnection);
        Assert.Equal(SessionStartResult.PreviousSessionNotReleased, Start());

        removed.SetResult();
        await WaitForAsync(_ => _coordinator.IsReleased, pollCounters: true);
        Assert.Equal(SessionStartResult.Started, Start());
    }

    [Fact]
    public async Task An_open_abandoned_at_the_teardown_deadline_blocks_a_new_session_until_it_returns_and_keeps_nothing()
    {
        // adb answering after the deadline: a prepare that ignores cancellation.
        Compose(coordinator: options => options with { TeardownDeadline = TimeSpan.FromMilliseconds(300) });
        var adbAnswers = new TaskCompletionSource<AttemptOutcome<TransportEndpoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.Prepare = (_, _) => adbAnswers.Task;
        Start();
        await WaitForAsync(_ => _transport.Prepares == 1, pollCounters: true);

        Assert.True(_coordinator.Stop());
        await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);

        Assert.False(_coordinator.IsReleased);
        Assert.Contains(_coordinator.LastTeardownFaults, fault => fault.Code == SessionFaultCodes.TeardownStepOverran);
        Assert.Equal(SessionStartResult.PreviousSessionNotReleased, Start());

        // adb answers at last. The open's token was cancelled when the session began to end, so it
        // does not even connect: nothing is made that would then have to be undone.
        adbAnswers.SetResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(_transport.EndPoint)));

        await WaitForAsync(_ => _coordinator.IsReleased, pollCounters: true);
        Assert.False(_connection.HasConnection);
        Assert.False(_transport.Listener.Pending(), "the abandoned open connected after the session had ended");
        _transport.Prepare = null;
        Assert.Equal(SessionStartResult.Started, Start());
        using TestClient client = await AcceptAsync();
        await client.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming);
    }

    // ---- Helpers ----

    private void Compose(
        Func<HostConnectionOptions, HostConnectionOptions>? connection = null,
        Func<SessionCoordinatorOptions, SessionCoordinatorOptions>? coordinator = null)
    {
        var connectionOptions = new HostConnectionOptions { DeviceName = "TEST-HOST", AppVersion = "1.2.3" };
        _pipeline = new FakePipeline(_log);
        _connection = new HostConnectionController(_transport, connection?.Invoke(connectionOptions) ?? connectionOptions);
        var coordinatorOptions = new SessionCoordinatorOptions { RetryBudget = TimeSpan.FromSeconds(8) };
        _coordinator = new SessionCoordinator(new SessionStateMachine(), _connection, _pipeline, new SystemSessionClock(), coordinator?.Invoke(coordinatorOptions) ?? coordinatorOptions);
        _coordinator.StateChanged += (_, _) =>
        {
            lock (_statesGate)
            {
                _states.Add(_coordinator.State);
            }
        };
    }

    private SessionStartResult Start() => _coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror));

    private SessionPhase[] Phases()
    {
        lock (_statesGate)
        {
            return [.. _states.Select(state => state.Phase)];
        }
    }

    private SessionFault[] _states_Faults()
    {
        lock (_statesGate)
        {
            return [.. _states.Select(state => state.LastAttemptFault).OfType<SessionFault>()];
        }
    }

    private async Task<TestClient> AcceptAsync()
    {
        TestClient client = await _transport.AcceptAsync();
        _cleanup.Add(client);
        return client;
    }

    private async Task<TestClient> StreamingAsync()
    {
        Assert.Equal(SessionStartResult.Started, Start());
        TestClient client = await AcceptAsync();
        await client.HandshakeAsync();
        await WaitForStreamingAsync(keyframes: 1);
        return client;
    }

    /// <summary>
    /// Streaming, and the keyframe that entering it asks for has been asked for. The coordinator
    /// asks just after it reports the new phase, so the count is polled rather than read at the
    /// moment the phase is seen.
    /// </summary>
    private Task<SessionState> WaitForStreamingAsync(int keyframes) =>
        WaitForAsync(state => state.Phase == SessionPhase.Streaming && _pipeline.KeyframeRequests >= keyframes, pollCounters: true);

    /// <summary>
    /// Waits for the coordinator's state to satisfy <paramref name="condition"/>, by its own
    /// notification. A condition on a counter that has no notification is polled instead
    /// (<paramref name="pollCounters"/>). Either way the failsafe ends a test that would hang.
    /// </summary>
    private async Task<SessionState> WaitForAsync(Func<SessionState, bool> condition, bool pollCounters = false)
    {
        var reached = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            SessionState state = _coordinator.State;
            if (condition(state))
            {
                reached.TrySetResult(state);
            }
        }

        _coordinator.StateChanged += Check;
        try
        {
            Check(null, EventArgs.Empty);
            if (pollCounters)
            {
                long deadline = Environment.TickCount64 + (long)Loopback.Patience.TotalMilliseconds;
                while (!reached.Task.IsCompleted && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                    Check(null, EventArgs.Empty);
                }
            }

            try
            {
                return await reached.Task.Within();
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Not reached within {Loopback.Patience.TotalSeconds:0} s. Phases: {string.Join(", ", Phases())}. Calls: {string.Join(", ", _log.Entries)}");
                throw;
            }
        }
        finally
        {
            _coordinator.StateChanged -= Check;
        }
    }

    private async Task ServeEveryConnectionAsync(CancellationToken stop)
    {
        var served = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            System.Net.Sockets.Socket socket;
            try
            {
                socket = await _transport.Listener.AcceptSocketAsync(stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            served.Add(Task.Run(async () =>
            {
                using var client = new TestClient(socket);
                try
                {
                    await client.HandshakeAsync();
                    await client.ReadUntilClosedAsync();
                }
                catch (Exception exception) when (exception is EndOfStreamException or System.Net.Sockets.SocketException or Xunit.Sdk.XunitException or ObjectDisposedException)
                {
                    // The host closed the connection part-way through the handshake: the race this test is about.
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(served).Within();
    }
}
