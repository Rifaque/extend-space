namespace ExtendSpace.Core.Sessions;

/// <summary>
/// Owns the state of the single session the host can hold. The UI dispatches user intent and the
/// session coordinator dispatches what actually happened; both observe <see cref="StateChanged"/>.
/// </summary>
public sealed class SessionStateMachine
{
    private readonly Lock _gate = new();
    private SessionState _state = SessionState.Disconnected;

    /// <summary>Raised after every accepted event, on the thread that dispatched it.</summary>
    public event EventHandler? StateChanged;

    public SessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="sessionEvent"/>. Returns false, leaving the state unchanged, if the
    /// event is not valid in the current phase.
    /// </summary>
    public bool Dispatch(SessionEvent sessionEvent)
    {
        lock (_gate)
        {
            if (Next(_state, sessionEvent) is not { } next)
            {
                return false;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Returns the state that follows, or null if the event does not apply.</summary>
    public static SessionState? Next(SessionState state, SessionEvent sessionEvent) =>
        (state.Phase, sessionEvent) switch
        {
            (SessionPhase.Disconnected, SessionEvent.ConnectRequested requested) =>
                new SessionState(SessionPhase.Connecting, requested.Request),

            (SessionPhase.Connecting or SessionPhase.Reconnecting, SessionEvent.TransportConnected) =>
                state with { Phase = SessionPhase.Negotiating },

            (SessionPhase.Negotiating, SessionEvent.NegotiationCompleted) =>
                state with { Phase = SessionPhase.Streaming },

            (SessionPhase.Streaming, SessionEvent.ConnectionLost) =>
                state with { Phase = SessionPhase.Reconnecting },

            (SessionPhase.Disconnecting, SessionEvent.TeardownCompleted) =>
                SessionState.Disconnected,

            (SessionPhase.Failed, SessionEvent.ErrorAcknowledged) =>
                SessionState.Disconnected,

            // The user can always disconnect an in-progress session, and anything in progress can fail.
            (_, SessionEvent.DisconnectRequested) when IsInProgress(state.Phase) =>
                state with { Phase = SessionPhase.Disconnecting },

            (_, SessionEvent.Failed failed) when IsInProgress(state.Phase) =>
                new SessionState(SessionPhase.Failed, state.Request, failed.Error),

            _ => null,
        };

    private static bool IsInProgress(SessionPhase phase) =>
        phase is SessionPhase.Connecting
            or SessionPhase.Negotiating
            or SessionPhase.Streaming
            or SessionPhase.Reconnecting;
}
