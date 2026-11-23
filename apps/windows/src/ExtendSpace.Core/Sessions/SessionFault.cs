namespace ExtendSpace.Core.Sessions;

/// <summary>
/// Which part of a session a fault came from. The list is what the host can actually observe;
/// a decoder fault happens on the client and reaches the host as a <see cref="Protocol"/> fault.
/// </summary>
public enum FaultCategory
{
    /// <summary>The display source: monitor gone or resized, capture not available.</summary>
    Capture,

    /// <summary>No encoder could be opened, or the encoder failed while running.</summary>
    Encoder,

    /// <summary>The route to the device could not be set up: for USB, adb and its port forward.</summary>
    Transport,

    /// <summary>An opened connection was refused, closed or reset.</summary>
    Connection,

    /// <summary>The peer broke the protocol, speaks another version, or rejected the stream.</summary>
    Protocol,

    /// <summary>Something that had to happen within a time did not: a handshake step, a keepalive.</summary>
    Timeout,

    /// <summary>A teardown step failed or overran. Recorded; teardown carries on.</summary>
    Cleanup,

    /// <summary>Anything unexpected. Carries the exception's type and message.</summary>
    Internal,
}

/// <summary>What the session's owner may do about a fault.</summary>
public enum FaultTreatment
{
    /// <summary>
    /// The cause can change by itself (nothing listening yet, device not attached, connection
    /// dropped), so another attempt is worth making while the retry budget lasts.
    /// </summary>
    Retryable,

    /// <summary>Trying again cannot help. The session ends with this fault.</summary>
    Terminal,
}

/// <summary>
/// One thing that went wrong in a session, as the component that saw it reports it to the
/// session's owner. It says where, what and whether trying again can help, and nothing about how
/// the transport or the media pipeline is implemented.
///
/// Cancellation is not a fault and has no representation here: an operation that was cancelled
/// reports <see cref="OutcomeKind.Cancelled"/>.
/// </summary>
/// <param name="Code">Stable, lower_snake_case, for logs and tests; for example <c>no_app_listening</c>.</param>
/// <param name="Message">A sentence for a log or a status line. Never video data, paths or device serials.</param>
/// <param name="UserActionable">True when the person at the PC can do something about it and should be told what.</param>
public sealed record SessionFault(
    FaultCategory Category,
    string Code,
    string Message,
    FaultTreatment Treatment,
    bool UserActionable = false)
{
    public bool IsRetryable => Treatment == FaultTreatment.Retryable;

    /// <summary>
    /// An exception nothing expected. It keeps the exception's type and message, so that the
    /// record of the session says what happened rather than that "something" did.
    /// </summary>
    public static SessionFault Unexpected(Exception exception) => new(
        FaultCategory.Internal,
        "unexpected_exception",
        $"{exception.GetType().Name}: {exception.Message}",
        FaultTreatment.Terminal);
}

public enum OutcomeKind
{
    Succeeded,
    Failed,

    /// <summary>The caller's cancellation was observed. Nothing went wrong and nothing is to be reported as an error.</summary>
    Cancelled,

    /// <summary>
    /// The peer ended the session in an orderly way while the step was waiting for it: the client
    /// sent <c>bye</c> during the handshake (protocol sections 7 and 14). Not a fault and not to be
    /// retried: the session ends as the client asked (decision D44).
    /// </summary>
    PeerEnded,
}

/// <summary>How one step of a session attempt ended. Exactly one of the three; a fault only when failed.</summary>
public sealed record AttemptOutcome
{
    private AttemptOutcome(OutcomeKind kind, SessionFault? fault, string? peerReason = null)
    {
        Kind = kind;
        Fault = fault;
        PeerReason = peerReason;
    }

    public OutcomeKind Kind { get; }

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="OutcomeKind.Failed"/>.</summary>
    public SessionFault? Fault { get; }

    public static AttemptOutcome Succeeded { get; } = new(OutcomeKind.Succeeded, null);

    public static AttemptOutcome Cancelled { get; } = new(OutcomeKind.Cancelled, null);

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="OutcomeKind.PeerEnded"/>: the peer's own words, for logs.</summary>
    public string? PeerReason { get; }

    public static AttemptOutcome Failed(SessionFault fault) => new(OutcomeKind.Failed, fault ?? throw new ArgumentNullException(nameof(fault)));

    public static AttemptOutcome PeerEnded(string reason) => new(OutcomeKind.PeerEnded, null, reason ?? throw new ArgumentNullException(nameof(reason)));
}

/// <summary>As <see cref="AttemptOutcome"/>, for a step that produces something when it succeeds.</summary>
public sealed record AttemptOutcome<T>
    where T : class
{
    private AttemptOutcome(OutcomeKind kind, T? value, SessionFault? fault, string? peerReason = null)
    {
        Kind = kind;
        Value = value;
        Fault = fault;
        PeerReason = peerReason;
    }

    public OutcomeKind Kind { get; }

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="OutcomeKind.Succeeded"/>.</summary>
    public T? Value { get; }

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="OutcomeKind.Failed"/>.</summary>
    public SessionFault? Fault { get; }

    public static AttemptOutcome<T> Cancelled { get; } = new(OutcomeKind.Cancelled, null, null);

    public static AttemptOutcome<T> Succeeded(T value) => new(OutcomeKind.Succeeded, value ?? throw new ArgumentNullException(nameof(value)), null);

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="OutcomeKind.PeerEnded"/>: the peer's own words, for logs.</summary>
    public string? PeerReason { get; }

    public static AttemptOutcome<T> Failed(SessionFault fault) => new(OutcomeKind.Failed, null, fault ?? throw new ArgumentNullException(nameof(fault)));

    public static AttemptOutcome<T> PeerEnded(string reason) => new(OutcomeKind.PeerEnded, null, null, reason ?? throw new ArgumentNullException(nameof(reason)));
}
