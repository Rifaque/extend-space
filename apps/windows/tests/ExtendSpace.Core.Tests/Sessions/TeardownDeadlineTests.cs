using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

/// <summary>
/// The teardown deadline is one absolute point on the session clock, set when teardown begins
/// (decision D26): every step is awaited only until then, whatever bound the component has of its
/// own, so the session reaches a terminal phase by the deadline however the steps behave. A step
/// still running at the deadline is abandoned, recorded, and keeps the session "not released"
/// until it returns. All on the manual clock, on the test's thread, with no real time.
/// </summary>
public class TeardownDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void When_every_step_returns_at_once_teardown_takes_no_time_at_all()
    {
        var harness = new CoordinatorHarness().Streaming();
        TimeSpan began = harness.Clock.Elapsed;

        harness.Coordinator.Stop();

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(began, harness.Clock.Elapsed);
        Assert.Empty(harness.Coordinator.LastTeardownFaults);
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Theory]
    [InlineData(9_999, 0)]
    [InlineData(0, 9_999)]
    [InlineData(5_000, 4_999)]
    public void Steps_that_together_use_almost_the_whole_deadline_are_waited_for(int pipelineMillis, int closeMillis)
    {
        var harness = new CoordinatorHarness();
        var stop = new TaskCompletionSource();
        var close = new TaskCompletionSource();
        harness.Pipeline.Stop = _ => stop.Task;
        harness.Connection.Close = _ => close.Task;
        harness.Streaming();
        TimeSpan began = harness.Clock.Elapsed;

        harness.Coordinator.Stop();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(pipelineMillis));
        stop.SetResult();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(closeMillis));
        Assert.Equal(SessionPhase.Disconnecting, harness.Phase);
        close.SetResult();

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(began + TimeSpan.FromMilliseconds(pipelineMillis + closeMillis), harness.Clock.Elapsed);
        Assert.Empty(harness.Coordinator.LastTeardownFaults);
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Fact]
    public void A_later_step_has_only_what_the_earlier_ones_left_and_is_abandoned_when_that_runs_out()
    {
        // The per-step bounds do not add up: the close gets the 5 s the pipeline left, not a bound of its own.
        var harness = new CoordinatorHarness();
        var stop = new TaskCompletionSource();
        var close = new TaskCompletionSource();
        harness.Pipeline.Stop = _ => stop.Task;
        harness.Connection.Close = _ => close.Task;
        harness.Streaming();
        TimeSpan began = harness.Clock.Elapsed;

        harness.Coordinator.Stop();
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        stop.SetResult();
        harness.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(SessionPhase.Disconnecting, harness.Phase);
        harness.Clock.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(began + Deadline, harness.Clock.Elapsed);
        Assert.Contains("close the connection", Assert.Single(harness.Coordinator.LastTeardownFaults).Message);
        Assert.False(harness.Coordinator.IsReleased);

        close.SetResult();
        Assert.True(harness.Coordinator.IsReleased);
        harness.AssertSettled();
    }

    [Fact]
    public void A_step_that_starts_with_no_time_left_is_still_started_told_to_hurry_and_abandoned_at_once()
    {
        // The pipeline uses the whole deadline. The connection is closed anyway, because only that
        // releases the socket and the forward; it is given an already-cancelled token, and the
        // session is terminal without waiting for it.
        var harness = new CoordinatorHarness();
        var close = new TaskCompletionSource();
        CancellationToken closeToken = default;
        harness.Pipeline.Stop = _ => new TaskCompletionSource().Task;
        harness.Connection.Close = token =>
        {
            closeToken = token;
            return close.Task;
        };
        harness.Streaming();
        TimeSpan began = harness.Clock.Elapsed;

        harness.Coordinator.Stop();
        harness.Clock.Advance(Deadline);

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(began + Deadline, harness.Clock.Elapsed);
        Assert.Equal(1, harness.Connection.Closes);
        Assert.True(closeToken.IsCancellationRequested);
        Assert.Equal(["stop the video pipeline", "close the connection"], harness.Coordinator.LastTeardownFaults.Select(fault => fault.Message.Split('\'')[1]));
        Assert.Equal(0, harness.Clock.PendingDelays);
        Assert.Equal(SessionStartResult.PreviousSessionNotReleased, harness.Start());
    }

    [Fact]
    public void A_close_that_cannot_finish_in_time_releases_the_session_late_and_until_then_no_new_session_starts()
    {
        // The connection's close ends with removing the adb forward, which is bounded by adb and
        // not by the coordinator. The session does not wait for it past the deadline; the next one
        // waits for it instead, so two sessions never hold a forward or a socket at once.
        var harness = new CoordinatorHarness();
        var close = new TaskCompletionSource();
        harness.Connection.Close = _ => close.Task;
        harness.Streaming();

        harness.Coordinator.Stop();
        harness.Clock.Advance(Deadline);

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Equal(SessionStartResult.PreviousSessionNotReleased, harness.Start());
        Assert.Equal(1, harness.Connection.Opens);

        close.SetResult();
        Assert.Equal(SessionStartResult.Started, harness.Start());
        Assert.Equal(SessionPhase.Streaming, harness.Phase);
        harness.AssertSettled();
    }

    [Fact]
    public void A_cleanup_step_that_fails_is_recorded_beside_the_fault_that_ended_the_session_and_does_not_replace_it()
    {
        var harness = new CoordinatorHarness();
        harness.Connection.Close = _ => Task.FromException(new IOException("adb could not remove the forward"));
        harness.Streaming();

        harness.Pipeline.Fail(Faults.MonitorResized);

        Assert.Equal(SessionPhase.Failed, harness.Phase);
        Assert.Equal((SessionErrorCode.CaptureUnavailable, Faults.MonitorResized), (harness.State.Error!.Code, harness.State.Error.Cause));
        SessionFault cleanup = Assert.Single(harness.Coordinator.LastTeardownFaults);
        Assert.Equal(SessionFaultCodes.TeardownStepFailed, cleanup.Code);
        Assert.Contains("adb could not remove the forward", cleanup.Message);
        harness.AssertSettled();
    }

    [Fact]
    public void An_open_abandoned_at_the_deadline_that_later_connects_is_closed_by_the_coordinator_before_the_session_counts_as_released()
    {
        var harness = new CoordinatorHarness();
        var open = new TaskCompletionSource<AttemptOutcome>();
        harness.Connection.Open = (_, _) => open.Task;
        harness.Start();

        harness.Coordinator.Stop();
        harness.Clock.Advance(Deadline);
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        int closesAtDeadline = harness.Connection.Closes;
        Assert.False(harness.Coordinator.IsReleased);

        open.SetResult(AttemptOutcome.Succeeded);

        Assert.Equal(closesAtDeadline + 1, harness.Connection.Closes);
        Assert.Equal("the session had already ended", harness.Connection.LastClose!.Value.Reason);
        Assert.True(harness.Coordinator.IsReleased);
        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
    }

    [Fact]
    public void A_loss_then_stops_racing_the_teardown_close_once_reconnect_never_and_end_stopped()
    {
        var harness = new CoordinatorHarness();
        var close = new TaskCompletionSource();
        harness.Connection.Close = _ => close.Task;
        harness.Streaming();

        harness.Connection.End(new ConnectionEnd(ConnectionEndKind.Lost, Faults.Reset));
        Assert.Equal(SessionPhase.Reconnecting, harness.Phase);
        Assert.True(harness.Coordinator.Stop());
        Assert.False(harness.Coordinator.Stop());
        harness.Connection.RaiseKeyframeWanted();
        close.SetResult();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(SessionPhase.Disconnected, harness.Phase);
        Assert.Null(harness.State.Error);
        Assert.Equal((1, 1, 1), (harness.Connection.Opens, harness.Connection.Closes, harness.Pipeline.Stops));
        Assert.False(harness.Coordinator.AcknowledgeError());
        harness.AssertSettled();
    }
}
