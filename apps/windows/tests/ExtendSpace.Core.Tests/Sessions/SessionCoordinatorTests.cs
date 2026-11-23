using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

// Every test here runs the coordinator on the test's own thread: the fakes complete inline and the
// clock moves only when the test moves it. No test sleeps, and none needs a timeout of its own.
// The one exception, at the end of the file, uses real threads on purpose and says so.

public class SessionCoordinatorStartupTests
{
    [Fact]
    public void A_clean_start_runs_the_six_steps_in_order_and_asks_for_one_keyframe_last()
    {
        var harness = new CoordinatorHarness();

        Assert.Equal(SessionStartResult.Started, harness.Start());

        Assert.Equal(
            [
                "state Connecting",
                "pipeline.Acquire",
                "pipeline.WatchFault",
                "connection.Open",
                "state Negotiating",
                "connection.Exchange",
                "pipeline.Prepare 1920x1080@60",
                "connection.Configure 1920x1080",
                "state Streaming",
                "pipeline.Start",
                "pipeline.Encoding on",
                "pipeline.Keyframe",
                "connection.WaitForEnd",
            ],
            harness.Log.Entries);
        Assert.Equal(new StreamSettings(1920, 1080, 60, 12_000_000, "hardware encoder", true), harness.Connection.LastConfigured);
        Assert.True(harness.State.HasStreamed);
        Assert.Null(harness.State.LastAttemptFault);
        harness.AssertSettled();
    }

    [Fact]
    public void The_whole_coordinator_runs_on_the_thread_that_drives_it()
    {
        var harness = new CoordinatorHarness();
        int testThread = Environment.CurrentManagedThreadId;
        var threads = new HashSet<int>();
        harness.Coordinator.StateChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);

        harness.Streaming();
        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
        harness.Coordinator.Stop();

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal([testThread], threads);
    }

    [Fact]
    public void Encoded_video_goes_from_the_pipeline_to_the_connection_without_the_coordinator()
    {
        var harness = new CoordinatorHarness().Streaming();

        harness.Pipeline.Capture();
        harness.Pipeline.Capture();

        Assert.Equal(2, harness.Connection.FramesOffered);
    }

    [Fact]
    public void A_client_whose_decoders_are_too_small_for_1080p_gets_the_measured_720p30_fallback()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Exchange = (_, _) => Reply.Ok(FakeConnection.SmallDecoderTablet);

        harness.Streaming();

        Assert.Contains("pipeline.Prepare 1280x720@30", harness.Log.Entries);
        Assert.Equal(6_000_000, harness.Connection.LastConfigured!.BitrateBitsPerSecond);
    }

    [Fact]
    public void A_client_that_can_decode_nothing_offered_ends_the_session_without_opening_an_encoder()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Exchange = (_, _) => Reply.Ok(FakeConnection.TinyDecoderTablet);

        harness.Start();

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.NegotiationFailed, harness.State.Error!.Code);
        Assert.Equal(SessionFaultCodes.NoCommonConfiguration, harness.State.Error.Cause!.Code);
        Assert.Equal(0, harness.Pipeline.Prepares);

        // The client is told why, through the connection's last message.
        Assert.Equal(SessionFaultCodes.NoCommonConfiguration, harness.Connection.LastClose!.Value.Fault!.Code);
        harness.AssertSettled();
    }

    public static TheoryData<string> TerminalSteps => ["acquire", "open", "exchange", "prepare", "configure"];

    [Theory]
    [MemberData(nameof(TerminalSteps))]
    public void A_step_that_fails_terminally_undoes_exactly_what_was_done_once_each_then_fails(string step)
    {
        var harness = new CoordinatorHarness();
        (SessionFault fault, SessionErrorCode code) = step switch
        {
            "acquire" => (Faults.MonitorGone, SessionErrorCode.CaptureUnavailable),
            "open" => (Faults.AdbMissing, SessionErrorCode.TransportUnavailable),
            "exchange" => (Faults.VersionMismatch, SessionErrorCode.IncompatibleProtocol),
            "prepare" => (Faults.NoEncoder, SessionErrorCode.EncoderUnavailable),
            _ => (Faults.StreamRejected, SessionErrorCode.NegotiationFailed),
        };
        switch (step)
        {
            case "acquire": harness.Pipeline.Acquire = _ => Reply.Fail<VideoSourceDescription>(fault); break;
            case "open": harness.Connection.Open = (_, _) => Reply.Fail(fault); break;
            case "exchange": harness.Connection.Exchange = (_, _) => Reply.Fail<ClientCapabilities>(fault); break;
            case "prepare": harness.Pipeline.Prepare = (_, _) => Reply.Fail<StreamSettings>(fault); break;
            default: harness.Connection.Configure = (_, _) => Reply.Fail<PeerDescription>(fault); break;
        }

        harness.Start();

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(code, harness.State.Error!.Code);
        Assert.Equal(fault.Code, harness.State.Error.Cause!.Code);
        Assert.Equal(FaultTreatment.Terminal, harness.State.Error.Cause.Treatment);

        // Nothing is retried and nothing is attempted after the failing step.
        Assert.DoesNotContain(SessionPhase.Streaming, harness.Phases);
        Assert.Equal(0, harness.Pipeline.Starts);
        Assert.Equal(step == "acquire" ? 0 : 1, harness.Connection.Opens);

        // Undone in reverse order of acquisition, each once: the pipeline only if it acquired its
        // source, the connection only if one was opened; Failed comes after both.
        Assert.Equal(step == "acquire" ? 0 : 1, harness.Pipeline.Stops);
        Assert.Equal(step == "acquire" ? 0 : 1, harness.Connection.Closes);
        if (step != "acquire")
        {
            Assert.True(harness.Log.IndexOf("pipeline.Stop") < harness.Log.IndexOf("connection.Close"));
            Assert.Equal(fault.Code, harness.Connection.LastClose!.Value.Fault!.Code);
        }

        Assert.Equal("state Failed", harness.Log.Entries[^1]);
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Theory]
    [InlineData("open")]
    [InlineData("exchange")]
    [InlineData("configure")]
    public void A_step_that_fails_retryably_is_reported_waited_out_and_retried_from_open(string step)
    {
        var harness = new CoordinatorHarness();
        SessionFault fault = step == "open" ? Faults.NothingListening : Faults.HandshakeTimeout;
        switch (step)
        {
            case "open": harness.Connection.Open = (n, _) => n == 1 ? Reply.Fail(fault) : Reply.Ok(); break;
            case "exchange": harness.Connection.Exchange = (n, _) => n == 1 ? Reply.Fail<ClientCapabilities>(fault) : Reply.Ok(FakeConnection.Tablet); break;
            default:
                int configures = 0;
                harness.Connection.Configure = (_, _) => ++configures == 1 ? Reply.Fail<PeerDescription>(fault) : Reply.Ok(FakeConnection.Peer);
                break;
        }

        harness.Start();

        // A first session that never streamed goes back to Connecting, keeping the fault visible.
        Assert.Equal(SessionPhase.Connecting, harness.Phase);
        Assert.Equal(fault, harness.State.LastAttemptFault);
        Assert.Equal(1, harness.Clock.PendingDelays);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(1, harness.Connection.Opens);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, harness.Connection.Opens);
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        Assert.Null(harness.State.LastAttemptFault);

        // The source is acquired once, and an encoder prepared before a failed configure is kept.
        Assert.Equal(1, harness.Pipeline.Acquires);
        Assert.Equal(1, harness.Pipeline.Prepares);
        Assert.Equal(0, harness.Connection.Closes);
        harness.AssertSettled();
    }

    [Fact]
    public void Attempts_back_off_from_250_ms_doubling_to_4_s_and_the_session_fails_when_the_budget_is_spent()
    {
        var harness = new CoordinatorHarness();
        var attempts = new List<double>();
        harness.Connection.Open = (_, _) =>
        {
            attempts.Add(harness.Clock.Elapsed.TotalSeconds);
            return Reply.Fail(Faults.NothingListening);
        };

        harness.Start();
        for (int tick = 0; tick < 3_100 && harness.Phase != SessionPhase.Failed; tick++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal([0, 0.25, 0.75, 1.75, 3.75, 7.75, 11.75, 15.75, 19.75, 23.75, 27.75, 30], attempts);
        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(TimeSpan.FromSeconds(30), harness.Clock.Elapsed);

        SessionError error = harness.State.Error!;
        Assert.Equal(SessionErrorCode.TransportUnavailable, error.Code);
        Assert.Equal("No connection could be established within 30 s.", error.Detail);
        Assert.Equal(Faults.NothingListening, error.Cause);

        // There is nobody to tell: the connection is closed without a fatal message.
        Assert.Null(harness.Connection.LastClose!.Value.Fault);
        Assert.Equal(1, harness.Pipeline.Stops);
        harness.AssertSettled();
    }

    [Fact]
    public void A_retry_never_starts_one_tick_before_its_backoff_has_passed()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Open = (_, _) => Reply.Fail(Faults.NothingListening);
        harness.Start();

        TimeSpan[] delays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)];
        int expected = 1;
        foreach (TimeSpan delay in delays)
        {
            harness.Clock.Advance(delay - TimeSpan.FromTicks(1));
            Assert.Equal(expected, harness.Connection.Opens);
            harness.Clock.Advance(TimeSpan.FromTicks(1));
            Assert.Equal(++expected, harness.Connection.Opens);
        }
    }

    [Fact]
    public void A_cancellation_nobody_asked_for_is_not_retried_and_ends_the_session_as_internal()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Open = (_, _) => Task.FromResult(AttemptOutcome.Cancelled);

        harness.Start();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.Internal, harness.State.Error!.Code);
        Assert.Equal(SessionFaultCodes.UnrequestedCancellation, harness.State.Error.Cause!.Code);
        Assert.Equal(1, harness.Connection.Opens);
        Assert.DoesNotContain(harness.States, state => state.LastAttemptFault is not null);
        harness.AssertSettled();
    }

    [Fact]
    public void A_seam_that_throws_ends_the_session_with_the_exception_kept()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Exchange = (_, _) => throw new InvalidOperationException("the reader was disposed");

        harness.Start();

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.Internal, harness.State.Error!.Code);
        Assert.Equal("InvalidOperationException: the reader was disposed", harness.State.Error.Cause!.Message);
        harness.AssertSettled();
    }
}

public class SessionCoordinatorStopTests
{
    /// <summary>Each startup step, and the two waits, left pending by a well-behaved component until it is cancelled.</summary>
    public static TheoryData<string> PendingPoints => ["acquire", "open", "exchange", "prepare", "configure", "first backoff", "streaming", "reconnect backoff", "reconnect open"];

    [Theory]
    [MemberData(nameof(PendingPoints))]
    public void Stop_at_any_point_tears_down_in_order_ends_disconnected_and_nothing_starts_afterwards(string point)
    {
        var harness = new CoordinatorHarness();
        Arrange(harness, point);

        Assert.True(harness.Coordinator.Stop());

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Null(harness.State.Error);
        Assert.Equal([SessionPhase.Disconnecting, SessionPhase.Disconnected], harness.Phases.TakeLast(2));

        // After the stop only teardown calls appear, each at most once, pipeline first.
        IReadOnlyList<string> after = harness.Log.From("state Disconnecting");
        Assert.All(after, entry => Assert.Contains(entry, new[] { "state Disconnecting", "state Disconnected", "pipeline.Stop", "connection.Close", "connection ended: Closed" }));
        Assert.Equal(point == "acquire" ? 0 : 1, harness.Pipeline.Stops);
        Assert.Equal(point is "acquire" ? 0 : 1, harness.Connection.Closes);
        if (point != "acquire")
        {
            Assert.True(harness.Log.IndexOf("pipeline.Stop") < harness.Log.IndexOf("connection.Close"));
        }

        // Cancellation is not failure: no attempt is reported failed because of it, nothing waits.
        int failedAttemptsBefore = harness.States.Count(state => state.LastAttemptFault is not null);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(failedAttemptsBefore, harness.States.Count(state => state.LastAttemptFault is not null));
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.True(harness.Coordinator.IsReleased);
        Assert.Empty(harness.Coordinator.LastTeardownFaults);
        harness.AssertSettled();
    }

    private static void Arrange(CoordinatorHarness harness, string point)
    {
        switch (point)
        {
            case "acquire":
                harness.Pipeline.Acquire = Reply.UntilCancelled<VideoSourceDescription>;
                break;
            case "open":
                harness.Connection.Open = (_, token) => Reply.UntilCancelled(token);
                break;
            case "exchange":
                harness.Connection.Exchange = (_, token) => Reply.UntilCancelled<ClientCapabilities>(token);
                break;
            case "prepare":
                harness.Pipeline.Prepare = (_, token) => Reply.UntilCancelled<StreamSettings>(token);
                break;
            case "configure":
                harness.Connection.Configure = (_, token) => Reply.UntilCancelled<PeerDescription>(token);
                break;
            case "first backoff":
                harness.Connection.Open = (_, _) => Reply.Fail(Faults.NothingListening);
                break;
        }

        Assert.Equal(SessionStartResult.Started, harness.Start());

        if (point is "streaming" or "reconnect backoff" or "reconnect open")
        {
            Assert.Equal(SessionPhase.Streaming, harness.Phase);
        }

        if (point is "reconnect backoff" or "reconnect open")
        {
            if (point == "reconnect open")
            {
                harness.Connection.Open = (_, token) => Reply.UntilCancelled(token);
            }

            harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
            Assert.Equal(SessionPhase.Reconnecting, harness.Phase);
            if (point == "reconnect open")
            {
                harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
                Assert.Equal(2, harness.Connection.Opens);
            }
        }

        if (point is "first backoff" or "reconnect backoff")
        {
            Assert.Equal(1, harness.Clock.PendingDelays);
        }
    }

    [Fact]
    public void A_second_stop_and_a_stop_with_nothing_to_stop_are_refused_and_change_nothing()
    {
        var harness = new CoordinatorHarness();
        Assert.False(harness.Coordinator.Stop());

        harness.Pipeline.Stop = _ => new TaskCompletionSource().Task; // Holds teardown in Disconnecting.
        harness.Streaming();
        Assert.True(harness.Coordinator.Stop());
        Assert.Equal(SessionPhase.Disconnecting, harness.Phase);

        Assert.False(harness.Coordinator.Stop());
        Assert.False(harness.Coordinator.Stop());
        Assert.Equal(1, harness.Pipeline.Stops);
        Assert.Equal(SessionPhase.Disconnecting, harness.Phase);
    }

    [Fact]
    public void Starting_while_a_session_exists_is_refused_and_calls_nothing()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Open = (_, token) => Reply.UntilCancelled(token);
        harness.Start();

        Assert.Equal(SessionStartResult.AlreadyActive, harness.Start());

        harness.Connection.Open = (_, _) => Reply.Ok();
        harness.Coordinator.Stop();
        harness.Streaming();
        Assert.Equal(SessionStartResult.AlreadyActive, harness.Start());

        Assert.Equal(2, harness.Pipeline.Acquires);
        Assert.Equal(2, harness.Connection.Opens);
    }

    [Fact]
    public void A_failed_session_refuses_a_start_until_the_failure_is_acknowledged()
    {
        var harness = new CoordinatorHarness();
        harness.Pipeline.Acquire = _ => Reply.Fail<VideoSourceDescription>(Faults.MonitorGone);
        harness.Start();
        Assert.Equal(SessionPhase.Failed, harness.Phase);

        Assert.Equal(SessionStartResult.AlreadyActive, harness.Start());
        Assert.False(harness.Coordinator.Stop());

        Assert.True(harness.Coordinator.AcknowledgeError());
        harness.Pipeline.Acquire = _ => Reply.Ok(FakePipeline.Monitor);
        harness.Streaming();

        // The new session inherits nothing of the failed one.
        Assert.Null(harness.State.Error);
        Assert.Equal(1, harness.Pipeline.KeyframeRequests);
    }

    [Fact]
    public void A_stop_dispatched_by_someone_else_before_the_coordinator_reports_progress_still_tears_down_cleanly()
    {
        // Models a stop on another thread that has reached the state machine but whose input has
        // not reached the coordinator yet: the coordinator's next dispatch is the first to see it.
        var harness = new CoordinatorHarness();
        var open = new TaskCompletionSource<AttemptOutcome>();
        harness.Connection.Open = (_, _) => open.Task;
        harness.Start();

        Assert.True(harness.Machine.Dispatch(new SessionEvent.DisconnectRequested()));
        open.SetResult(AttemptOutcome.Succeeded);

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(0, harness.Connection.Exchanges);
        Assert.Equal(1, harness.Connection.Closes);
        Assert.Equal(1, harness.Pipeline.Stops);
        harness.AssertSettled();
    }
}

public class SessionCoordinatorStreamingTests
{
    [Fact]
    public void A_loss_while_streaming_keeps_source_and_encoder_and_reconnects_with_a_new_keyframe()
    {
        var harness = new CoordinatorHarness().Streaming();

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));

        Assert.Equal(SessionPhase.Reconnecting, harness.Phase);
        Assert.Equal(Faults.Reset, harness.State.LastAttemptFault);
        Assert.False(harness.Pipeline.Encoding);

        // The experiment waited the first backoff before the first reconnection attempt; so does this.
        Assert.Equal(1, harness.Connection.Opens);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        Assert.Null(harness.State.LastAttemptFault);
        Assert.Equal(2, harness.Connection.Exchanges);
        Assert.Equal((1, 1, 1), (harness.Pipeline.Acquires, harness.Pipeline.Prepares, harness.Pipeline.Starts));
        Assert.Equal(2, harness.Pipeline.KeyframeRequests);
        Assert.True(harness.Pipeline.Encoding);
        Assert.Equal(
            ["state Streaming", "pipeline.Encoding on", "pipeline.Keyframe", "connection.WaitForEnd"],
            harness.Log.Entries.TakeLast(4));
        harness.AssertSettled();
    }

    [Fact]
    public void After_a_reconnection_on_a_still_screen_a_keyframe_is_owed_with_encoding_on_and_nothing_captured()
    {
        var harness = new CoordinatorHarness().Streaming();
        harness.Pipeline.Capture();
        Assert.False(harness.Pipeline.KeyframeOwed);

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        // No picture arrived since the loss. What the coordinator must leave behind is an
        // obligation the pipeline cannot lose; producing it on a still screen is the pipeline's
        // contract (device test V3).
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        Assert.True(harness.Pipeline.Encoding);
        Assert.True(harness.Pipeline.KeyframeOwed);

        harness.Pipeline.Capture();
        Assert.False(harness.Pipeline.KeyframeOwed);
    }

    [Fact]
    public void A_returning_client_that_can_no_longer_take_the_stream_ends_the_session_without_re_preparing()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Exchange = (n, _) => Reply.Ok(n == 1 ? FakeConnection.Tablet : FakeConnection.TinyDecoderTablet);
        harness.Streaming();

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.NegotiationFailed, harness.State.Error!.Code);
        Assert.Equal(1, harness.Pipeline.Prepares);
        Assert.Equal(1, harness.Connection.Configures);
        harness.AssertSettled();
    }

    [Fact]
    public void A_lost_host_that_does_not_return_within_the_budget_fails_as_connection_lost()
    {
        var harness = new CoordinatorHarness().Streaming();
        harness.Connection.Open = (_, _) => Reply.Fail(Faults.NothingListening);

        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        for (int tick = 0; tick < 400 && harness.Phase != SessionPhase.Failed; tick++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(SessionPhase.Failed, harness.Phase);

        // The budget runs from the loss, not from the start of the session.
        Assert.Equal(TimeSpan.FromSeconds(40), harness.Clock.Elapsed);
        Assert.Equal(SessionErrorCode.ConnectionLost, harness.State.Error!.Code);
        Assert.Equal("No connection could be re-established within 30 s.", harness.State.Error.Detail);
        harness.AssertSettled();
    }

    [Fact]
    public void Reaching_streaming_again_gives_the_next_loss_a_whole_new_budget()
    {
        var harness = new CoordinatorHarness().Streaming();
        bool refuse = true;
        harness.Connection.Open = (_, _) => refuse ? Reply.Fail(Faults.NothingListening) : Reply.Ok();

        // Most of a budget spent failing, then a success.
        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromSeconds(25));
        refuse = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        TimeSpan secondLoss = harness.Clock.Elapsed;

        refuse = true;
        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SessionPhase.Reconnecting, harness.Phase);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(secondLoss + TimeSpan.FromSeconds(30), harness.Clock.Elapsed);
    }

    [Fact]
    public void A_renegotiation_that_fails_returns_to_reconnecting_not_connecting()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Exchange = (n, _) => n == 2 ? Reply.Fail<ClientCapabilities>(Faults.HandshakeTimeout) : Reply.Ok(FakeConnection.Tablet);
        harness.Streaming();

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(SessionPhase.Reconnecting, harness.Phase);
        Assert.Equal(Faults.HandshakeTimeout, harness.State.LastAttemptFault);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
    }

    [Fact]
    public void The_client_saying_goodbye_ends_the_session_without_error_or_retry()
    {
        var harness = new CoordinatorHarness().Streaming();

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.PeerEnded, PeerReason: "the screen was closed"));
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal([SessionPhase.Disconnecting, SessionPhase.Disconnected], harness.Phases.TakeLast(2));
        Assert.Null(harness.State.Error);
        Assert.Equal(1, harness.Connection.Opens);
        Assert.Equal("the device ended the session", harness.Connection.LastClose!.Value.Reason);
        harness.AssertSettled();
    }

    [Theory]
    [InlineData("exchange")]
    [InlineData("configure")]
    public void The_client_saying_goodbye_during_the_handshake_ends_the_session_as_a_stop_without_error_or_retry(string step)
    {
        // Decision D44: a bye is the orderly end of a session at any point after the preface
        // (protocol sections 7 and 14), so a handshake step that hears one ends the session exactly
        // as a bye while streaming does.
        var harness = new CoordinatorHarness();
        if (step == "exchange")
        {
            harness.Connection.Exchange = (_, _) => Task.FromResult(AttemptOutcome<ClientCapabilities>.PeerEnded("the user stopped waiting"));
        }
        else
        {
            harness.Connection.Configure = (_, _) => Task.FromResult(AttemptOutcome<PeerDescription>.PeerEnded("the user stopped waiting"));
        }

        harness.Start();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal([SessionPhase.Connecting, SessionPhase.Negotiating, SessionPhase.Disconnecting, SessionPhase.Disconnected], harness.Phases);
        Assert.Null(harness.State.Error);
        Assert.DoesNotContain(harness.States, state => state.LastAttemptFault is not null);
        Assert.Equal(1, harness.Connection.Opens);
        Assert.Equal(("the device ended the session", (SessionFault?)null), harness.Connection.LastClose!.Value);
        Assert.Equal(1, harness.Pipeline.Stops);
        harness.AssertSettled();
    }

    [Fact]
    public void A_goodbye_from_a_step_the_client_cannot_reach_is_a_broken_contract_not_a_stop()
    {
        var harness = new CoordinatorHarness();
        harness.Pipeline.Acquire = _ => Task.FromResult(AttemptOutcome<VideoSourceDescription>.PeerEnded("impossible"));

        harness.Start();

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionFaultCodes.UnexpectedPeerEnded, harness.State.Error!.Cause!.Code);
        harness.AssertSettled();
    }

    [Theory]
    [InlineData(ConnectionEndKind.Fatal)]
    [InlineData(ConnectionEndKind.Lost)]
    public void A_connection_that_ends_for_a_reason_that_cannot_be_cured_fails_the_session(ConnectionEndKind kind)
    {
        var harness = new CoordinatorHarness().Streaming();

        harness.Connection.End(new ConnectionEnd(kind, Faults.Malformed));

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.IncompatibleProtocol, harness.State.Error!.Code);
        Assert.Equal(1, harness.Connection.Opens);
        harness.AssertSettled();
    }

    [Fact]
    public void A_pipeline_fault_while_streaming_tears_down_and_tells_the_client_why()
    {
        var harness = new CoordinatorHarness().Streaming();

        harness.Pipeline.Fail(Faults.MonitorResized);

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.CaptureUnavailable, harness.State.Error!.Code);
        Assert.Equal(Faults.MonitorResized, harness.Connection.LastClose!.Value.Fault);
        Assert.Equal(["pipeline failed: monitor_resized", "pipeline.Stop", "connection.Close", "state Failed"], harness.Log.From("pipeline failed: monitor_resized"));
        harness.AssertSettled();
    }

    [Fact]
    public void A_pipeline_fault_during_startup_waits_for_the_call_in_progress_then_tears_down()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Open = (_, token) => Reply.UntilCancelled(token);
        harness.Start();

        harness.Pipeline.Fail(Faults.MonitorGone);

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal(SessionErrorCode.CaptureUnavailable, harness.State.Error!.Code);
        Assert.DoesNotContain(harness.States, state => state.LastAttemptFault is not null);
        harness.AssertSettled();
    }

    [Fact]
    public void Every_keyframe_the_client_wants_reaches_the_pipeline_once_there_is_a_source_and_none_after_the_session()
    {
        var harness = new CoordinatorHarness();
        var acquire = new TaskCompletionSource<AttemptOutcome<VideoSourceDescription>>();
        var open = new TaskCompletionSource<AttemptOutcome>();
        harness.Pipeline.Acquire = _ => acquire.Task;
        harness.Connection.Open = (_, _) => open.Task;
        harness.Start();

        // Before there is a source, the pipeline has nothing to make a keyframe of.
        harness.Connection.RaiseKeyframeWanted();
        Assert.Equal(0, harness.Pipeline.KeyframeRequests);

        // Before Streaming: passed on, and the pipeline owes it.
        acquire.SetResult(AttemptOutcome<VideoSourceDescription>.Succeeded(FakePipeline.Monitor));
        harness.Connection.RaiseKeyframeWanted();
        Assert.Equal(1, harness.Pipeline.KeyframeRequests);

        // The coordinator's own request on entering Streaming, then a burst from the client: none dropped.
        open.SetResult(AttemptOutcome.Succeeded);
        Assert.Equal(2, harness.Pipeline.KeyframeRequests);
        for (int i = 0; i < 5; i++)
        {
            harness.Connection.RaiseKeyframeWanted();
        }

        Assert.Equal(7, harness.Pipeline.KeyframeRequests);

        harness.Coordinator.Stop();
        harness.Connection.RaiseKeyframeWanted();
        Assert.Equal(7, harness.Pipeline.KeyframeRequests);
    }
}

public class SessionCoordinatorTeardownTests
{
    [Fact]
    public void A_teardown_step_that_never_returns_is_abandoned_at_the_deadline_and_blocks_new_sessions_until_it_does()
    {
        var harness = new CoordinatorHarness();
        var stuck = new TaskCompletionSource();
        harness.Pipeline.Stop = _ => stuck.Task;
        harness.Streaming();

        harness.Coordinator.Stop();
        harness.Clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Equal(SessionPhase.Disconnecting, harness.Phase);
        Assert.Equal(0, harness.Connection.Closes);

        harness.Clock.Advance(TimeSpan.FromTicks(1));

        // The deadline: a terminal phase whatever happens, and the steps after the stuck one still run.
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(1, harness.Connection.Closes);
        SessionFault cleanup = Assert.Single(harness.Coordinator.LastTeardownFaults);
        Assert.Equal((FaultCategory.Cleanup, SessionFaultCodes.TeardownStepOverran), (cleanup.Category, cleanup.Code));
        Assert.Contains("stop the video pipeline", cleanup.Message);

        // Nothing new may run beside the old capture session while it is still being released.
        Assert.False(harness.Coordinator.IsReleased);
        Assert.Equal(SessionStartResult.PreviousSessionNotReleased, harness.Start());
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(1, harness.Pipeline.Acquires);

        stuck.SetResult();
        Assert.True(harness.Coordinator.IsReleased);
        Assert.Equal(SessionStartResult.Started, harness.Start());
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        harness.AssertSettled();
    }

    [Fact]
    public void A_teardown_step_that_throws_is_recorded_and_teardown_carries_on()
    {
        var harness = new CoordinatorHarness();
        harness.Pipeline.Stop = _ => throw new InvalidOperationException("the device was removed");
        harness.Streaming();

        harness.Coordinator.Stop();

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(1, harness.Connection.Closes);
        SessionFault cleanup = Assert.Single(harness.Coordinator.LastTeardownFaults);
        Assert.Equal(SessionFaultCodes.TeardownStepFailed, cleanup.Code);
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Fact]
    public void A_call_that_ignores_cancellation_and_succeeds_late_has_what_it_acquired_released_before_a_new_start()
    {
        var harness = new CoordinatorHarness();
        var acquire = new TaskCompletionSource<AttemptOutcome<VideoSourceDescription>>();
        harness.Pipeline.Acquire = _ => acquire.Task;
        harness.Start();

        harness.Coordinator.Stop();
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Contains("acquire the display", Assert.Single(harness.Coordinator.LastTeardownFaults).Message);
        Assert.False(harness.Coordinator.IsReleased);
        int stopsAtDeadline = harness.Pipeline.Stops;

        // It succeeds after the session is over: what it acquired is released, once, by the coordinator.
        acquire.SetResult(AttemptOutcome<VideoSourceDescription>.Succeeded(FakePipeline.Monitor));
        Assert.Equal(stopsAtDeadline + 1, harness.Pipeline.Stops);
        Assert.True(harness.Coordinator.IsReleased);
        Assert.Equal(0, harness.Connection.Opens);
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
    }

    [Fact]
    public void A_call_that_ignores_cancellation_but_returns_inside_the_deadline_is_waited_for_and_then_undone()
    {
        var harness = new CoordinatorHarness();
        var acquire = new TaskCompletionSource<AttemptOutcome<VideoSourceDescription>>();
        harness.Pipeline.Acquire = _ => acquire.Task;
        harness.Start();

        harness.Coordinator.Stop();
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, harness.Pipeline.Stops);
        acquire.SetResult(AttemptOutcome<VideoSourceDescription>.Succeeded(FakePipeline.Monitor));

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(1, harness.Pipeline.Stops);
        Assert.Equal(0, harness.Connection.Opens);
        Assert.Empty(harness.Coordinator.LastTeardownFaults);
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Fact]
    public void A_stop_during_a_failure_teardown_ends_the_session_as_stopped_not_failed()
    {
        var harness = new CoordinatorHarness();
        var stop = new TaskCompletionSource();
        harness.Pipeline.Stop = _ => stop.Task;
        harness.Streaming();

        harness.Pipeline.Fail(Faults.MonitorResized);
        Assert.Equal(SessionPhase.Streaming, harness.Phase);

        Assert.True(harness.Coordinator.Stop());
        stop.SetResult();

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(1, harness.Pipeline.Stops);
        Assert.Equal(1, harness.Connection.Closes);
        harness.AssertSettled();
    }

    [Fact]
    public void Repeating_the_same_scenario_gives_the_same_record_every_time()
    {
        static IReadOnlyList<string> Scenario()
        {
            var harness = new CoordinatorHarness();
            harness.Connection.Open = (n, _) => n is 1 or 3 ? Reply.Fail(Faults.NothingListening) : Reply.Ok();
            harness.Start();
            harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
            harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            harness.Coordinator.Stop();
            return harness.Log.Entries;
        }

        IReadOnlyList<string> first = Scenario();
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(first, Scenario());
        }
    }
}

/// <summary>
/// The one place real threads are used: fakes that complete on the thread pool, as the real
/// components will, and intents from several threads at once. It checks what the deterministic
/// tests cannot: that the inbox really serialises, so that no seam sees overlapping calls and
/// every session reaches a terminal phase. It has a failsafe timeout, which nothing else has.
/// </summary>
public class SessionCoordinatorThreadingTests
{
    [Fact]
    public async Task Sessions_started_and_stopped_from_many_threads_never_overlap_and_always_end()
    {
        var log = new CallLog();
        var connection = new FakeConnection(log)
        {
            Open = async (_, token) =>
            {
                await Task.Yield();
                return token.IsCancellationRequested ? AttemptOutcome.Cancelled : Random.Shared.Next(4) == 0 ? AttemptOutcome.Failed(Faults.NothingListening) : AttemptOutcome.Succeeded;
            },
            Exchange = async (_, _) =>
            {
                await Task.Yield();
                return AttemptOutcome<ClientCapabilities>.Succeeded(FakeConnection.Tablet);
            },
            Close = async _ => await Task.Yield(),
        };
        var pipeline = new FakePipeline(log)
        {
            Acquire = async _ =>
            {
                await Task.Yield();
                return AttemptOutcome<VideoSourceDescription>.Succeeded(FakePipeline.Monitor);
            },
            Stop = async _ => await Task.Yield(),
        };
        var coordinator = new SessionCoordinator(new SessionStateMachine(), connection, pipeline, new SystemSessionClock(), new SessionCoordinatorOptions { RetryBudget = TimeSpan.FromSeconds(1) });
        using var failsafe = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        failsafe.CancelAfter(TimeSpan.FromSeconds(60));

        for (int round = 0; round < 100; round++)
        {
            Assert.Equal(SessionStartResult.Started, coordinator.Start(CoordinatorHarness.UsbMirror));
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
                async () =>
                {
                    await Task.Yield();
                    coordinator.Stop();
                    coordinator.Start(CoordinatorHarness.UsbMirror);
                },
                failsafe.Token)));
            coordinator.Stop();
            while (coordinator.State.Phase is not (SessionPhase.Disconnected or SessionPhase.Failed))
            {
                await Task.Delay(1, failsafe.Token);
                coordinator.Stop();
            }

            coordinator.AcknowledgeError();
        }

        Assert.True(coordinator.UnhandledInputFailures == 0, $"{coordinator.UnhandledInputFailures} coordinator input(s) threw");
        Assert.True(connection.Overlaps == 0, $"{connection.Overlaps} overlapping connection call(s)");
        Assert.True(pipeline.Overlaps == 0, $"{pipeline.Overlaps} overlapping pipeline call(s)");
        Assert.True(coordinator.IsReleased);
    }
}
