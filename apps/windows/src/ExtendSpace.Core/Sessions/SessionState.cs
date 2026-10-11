namespace ExtendSpace.Core.Sessions;

public enum TransportKind
{
    Usb,
    Wireless,
}

public enum DisplayMode
{
    /// <summary>Duplicate an existing Windows display.</summary>
    Mirror,

    /// <summary>Present the Android device to Windows as an additional monitor.</summary>
    Extend,
}

/// <summary>Session lifecycle. Mirrors the session states in protocol/specification/protocol.md.</summary>
public enum SessionPhase
{
    Disconnected,
    Connecting,
    Negotiating,
    Streaming,
    Reconnecting,
    Disconnecting,
    Failed,
}

public enum SessionErrorCode
{
    TransportUnavailable,
    AuthenticationFailed,
    IncompatibleProtocol,
    NegotiationFailed,
    ConnectionLost,
    Internal,
}

public sealed record SessionError(SessionErrorCode Code, string? Detail = null);

/// <summary>What the user asked for when starting a session.</summary>
public sealed record SessionRequest(TransportKind Transport, DisplayMode Mode);

/// <param name="Request">Set in every phase except <see cref="SessionPhase.Disconnected"/>.</param>
/// <param name="Error">Set only in <see cref="SessionPhase.Failed"/>.</param>
public sealed record SessionState(SessionPhase Phase, SessionRequest? Request = null, SessionError? Error = null)
{
    public static SessionState Disconnected { get; } = new(SessionPhase.Disconnected);
}

public abstract record SessionEvent
{
    private SessionEvent()
    {
    }

    public sealed record ConnectRequested(SessionRequest Request) : SessionEvent;

    public sealed record TransportConnected : SessionEvent;

    public sealed record NegotiationCompleted : SessionEvent;

    public sealed record ConnectionLost : SessionEvent;

    public sealed record DisconnectRequested : SessionEvent;

    public sealed record TeardownCompleted : SessionEvent;

    public sealed record Failed(SessionError Error) : SessionEvent;

    public sealed record ErrorAcknowledged : SessionEvent;
}
