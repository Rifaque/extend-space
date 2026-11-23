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

    /// <summary>The display could not be captured: the monitor is gone, changed size, or capture is not available.</summary>
    CaptureUnavailable,

    /// <summary>No video encoder could be opened for the stream, or the encoder failed while running.</summary>
    EncoderUnavailable,

    Internal,
}

/// <summary>Why a session failed, as the user is told.</summary>
/// <param name="Cause">The fault behind it, for diagnostics. The code above is the summary of it.</param>
public sealed record SessionError(SessionErrorCode Code, string? Detail = null, SessionFault? Cause = null);

/// <summary>What the user asked for when starting a session.</summary>
/// <param name="MonitorIndex">The monitor by its position in Windows' enumeration; for tools and tests.</param>
/// <param name="MonitorDeviceName">The monitor the user chose, by its device name (for example <c>\\.\DISPLAY2</c>); it wins over the index. Null for the main display (D68).</param>
public sealed record SessionRequest(TransportKind Transport, DisplayMode Mode, int? MonitorIndex = null, string? MonitorDeviceName = null);

/// <param name="Request">Set in every phase except <see cref="SessionPhase.Disconnected"/>.</param>
/// <param name="Error">Set only in <see cref="SessionPhase.Failed"/>.</param>
/// <param name="HasStreamed">
/// Whether this session has reached <see cref="SessionPhase.Streaming"/> at least once. It decides
/// where a failed attempt returns to. Always false outside the four in-progress phases.
/// </param>
/// <param name="LastAttemptFault">
/// Why the session is not streaming while it is still trying: the fault of the most recent failed
/// attempt, or of the loss that sent it to <see cref="SessionPhase.Reconnecting"/>. Cleared when
/// negotiation completes and when the session ends.
/// </param>
public sealed record SessionState(
    SessionPhase Phase,
    SessionRequest? Request = null,
    SessionError? Error = null,
    bool HasStreamed = false,
    SessionFault? LastAttemptFault = null)
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

    /// <summary>
    /// A streaming connection was lost without a goodbye. <paramref name="Fault"/> says why and must be
    /// retryable: a loss nothing can recover from is reported as <see cref="Failed"/>.
    /// </summary>
    public sealed record ConnectionLost(SessionFault Fault) : SessionEvent;

    /// <summary>
    /// One attempt to connect or to negotiate failed for a reason that may not last, and the
    /// session goes on. Not a wire message: it is the coordinator telling the state machine what
    /// happened. A fault that is not retryable is not an attempt failing; report it as <see cref="Failed"/>.
    /// </summary>
    public sealed record AttemptFailed(SessionFault Fault) : SessionEvent;

    public sealed record DisconnectRequested : SessionEvent;

    public sealed record TeardownCompleted : SessionEvent;

    public sealed record Failed(SessionError Error) : SessionEvent;

    public sealed record ErrorAcknowledged : SessionEvent;
}
