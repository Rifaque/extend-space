using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

public class SessionStateMachineTests
{
    private static readonly SessionRequest UsbMirror = new(TransportKind.Usb, DisplayMode.Mirror);
    private static readonly SessionError Lost = new(SessionErrorCode.ConnectionLost);
    private static readonly SessionFault Reset = new(FaultCategory.Connection, "connection_reset", "The connection was reset.", FaultTreatment.Retryable);

    private static SessionStateMachine MachineAfter(params SessionEvent[] events)
    {
        var machine = new SessionStateMachine();
        foreach (SessionEvent sessionEvent in events)
        {
            Assert.True(machine.Dispatch(sessionEvent), $"Setup event {sessionEvent} was rejected.");
        }

        return machine;
    }

    private static SessionEvent[] ToStreaming() =>
    [
        new SessionEvent.ConnectRequested(UsbMirror),
        new SessionEvent.TransportConnected(),
        new SessionEvent.NegotiationCompleted(),
    ];

    [Fact]
    public void Starts_disconnected()
    {
        Assert.Equal(SessionState.Disconnected, new SessionStateMachine().State);
    }

    [Fact]
    public void Connect_flows_through_negotiating_to_streaming_and_keeps_the_request()
    {
        var machine = new SessionStateMachine();
        var request = new SessionRequest(TransportKind.Wireless, DisplayMode.Extend);

        Assert.True(machine.Dispatch(new SessionEvent.ConnectRequested(request)));
        Assert.Equal(new SessionState(SessionPhase.Connecting, request), machine.State);

        Assert.True(machine.Dispatch(new SessionEvent.TransportConnected()));
        Assert.Equal(new SessionState(SessionPhase.Negotiating, request), machine.State);

        Assert.True(machine.Dispatch(new SessionEvent.NegotiationCompleted()));
        Assert.Equal(new SessionState(SessionPhase.Streaming, request, HasStreamed: true), machine.State);
    }

    [Fact]
    public void Losing_the_connection_while_streaming_starts_a_reconnect_that_renegotiates()
    {
        SessionStateMachine machine = MachineAfter(ToStreaming());

        Assert.True(machine.Dispatch(new SessionEvent.ConnectionLost(Reset)));
        Assert.Equal(new SessionState(SessionPhase.Reconnecting, UsbMirror, HasStreamed: true, LastAttemptFault: Reset), machine.State);

        Assert.True(machine.Dispatch(new SessionEvent.TransportConnected()));
        Assert.Equal(new SessionState(SessionPhase.Negotiating, UsbMirror, HasStreamed: true, LastAttemptFault: Reset), machine.State);
    }

    public static TheoryData<SessionPhase> InProgressPhases =>
    [
        SessionPhase.Connecting,
        SessionPhase.Negotiating,
        SessionPhase.Streaming,
        SessionPhase.Reconnecting,
    ];

    [Theory]
    [MemberData(nameof(InProgressPhases))]
    public void Every_in_progress_phase_can_be_disconnected_and_tears_down_to_disconnected(SessionPhase phase)
    {
        var state = new SessionState(phase, UsbMirror);

        SessionState? disconnecting = SessionStateMachine.Next(state, new SessionEvent.DisconnectRequested());
        Assert.Equal(new SessionState(SessionPhase.Disconnecting, UsbMirror), disconnecting);

        Assert.Equal(
            SessionState.Disconnected,
            SessionStateMachine.Next(disconnecting!, new SessionEvent.TeardownCompleted()));
    }

    [Theory]
    [MemberData(nameof(InProgressPhases))]
    public void Every_in_progress_phase_can_fail_and_keeps_the_request_and_error(SessionPhase phase)
    {
        var state = new SessionState(phase, UsbMirror);

        Assert.Equal(
            new SessionState(SessionPhase.Failed, UsbMirror, Lost),
            SessionStateMachine.Next(state, new SessionEvent.Failed(Lost)));
    }

    [Fact]
    public void A_failure_is_held_until_acknowledged()
    {
        SessionStateMachine machine = MachineAfter(new SessionEvent.ConnectRequested(UsbMirror), new SessionEvent.Failed(Lost));

        Assert.False(machine.Dispatch(new SessionEvent.ConnectRequested(UsbMirror)));
        Assert.Equal(SessionPhase.Failed, machine.State.Phase);

        Assert.True(machine.Dispatch(new SessionEvent.ErrorAcknowledged()));
        Assert.Equal(SessionState.Disconnected, machine.State);
    }

    [Fact]
    public void Events_that_do_not_apply_are_rejected_without_changing_state_or_notifying()
    {
        var machine = new SessionStateMachine();
        int notifications = 0;
        machine.StateChanged += (_, _) => notifications++;

        Assert.False(machine.Dispatch(new SessionEvent.TransportConnected()));
        Assert.False(machine.Dispatch(new SessionEvent.NegotiationCompleted()));
        Assert.False(machine.Dispatch(new SessionEvent.DisconnectRequested()));
        Assert.False(machine.Dispatch(new SessionEvent.Failed(Lost)));
        Assert.False(machine.Dispatch(new SessionEvent.TeardownCompleted()));

        Assert.Equal(SessionState.Disconnected, machine.State);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void A_second_connect_request_is_rejected_while_a_session_is_in_progress()
    {
        SessionStateMachine machine = MachineAfter(ToStreaming());

        Assert.False(machine.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Wireless, DisplayMode.Extend))));
        Assert.Equal(new SessionState(SessionPhase.Streaming, UsbMirror, HasStreamed: true), machine.State);
    }

    [Fact]
    public void Nothing_but_teardown_completion_leaves_disconnecting()
    {
        var state = new SessionState(SessionPhase.Disconnecting, UsbMirror);

        Assert.Null(SessionStateMachine.Next(state, new SessionEvent.ConnectRequested(UsbMirror)));
        Assert.Null(SessionStateMachine.Next(state, new SessionEvent.DisconnectRequested()));
        Assert.Null(SessionStateMachine.Next(state, new SessionEvent.Failed(Lost)));
    }

    [Fact]
    public void Each_accepted_event_raises_one_notification()
    {
        var machine = new SessionStateMachine();
        var observed = new List<SessionPhase>();
        machine.StateChanged += (_, _) => observed.Add(machine.State.Phase);

        foreach (SessionEvent sessionEvent in ToStreaming())
        {
            machine.Dispatch(sessionEvent);
        }

        Assert.Equal([SessionPhase.Connecting, SessionPhase.Negotiating, SessionPhase.Streaming], observed);
    }
}
