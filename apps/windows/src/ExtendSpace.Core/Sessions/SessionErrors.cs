namespace ExtendSpace.Core.Sessions;

/// <summary>
/// Fault codes the session's owner itself raises or acts on. Components choose their own codes;
/// these are the ones whose meaning the coordinator depends on, and the protocol's own error codes
/// (protocol section 12) where a fault carries one.
/// </summary>
public static class SessionFaultCodes
{
    /// <summary>Protocol: the client speaks no common major version.</summary>
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";

    /// <summary>Protocol: nothing the client can decode fits a configuration the host can send.</summary>
    public const string NoCommonConfiguration = "no_common_configuration";

    /// <summary>Protocol, terminal: the tablet does not trust this PC, or this PC does not trust the tablet (D76).</summary>
    public const string NotPaired = "not_paired";

    /// <summary>Protocol, terminal: the tablet's key is not the one paired with this PC (D76).</summary>
    public const string AuthenticationFailed = "authentication_failed";

    /// <summary>Protocol: the client could not apply the configuration it was sent.</summary>
    public const string StreamConfigRejected = "stream_config_rejected";

    /// <summary>Cleanup: a teardown step did not finish by the teardown deadline and was left running.</summary>
    public const string TeardownStepOverran = "teardown_step_overran";

    /// <summary>Cleanup: a teardown step threw.</summary>
    public const string TeardownStepFailed = "teardown_step_failed";

    /// <summary>Internal: a component reported cancellation that nobody asked for.</summary>
    public const string UnrequestedCancellation = "unrequested_cancellation";

    /// <summary>Internal: a step that cannot hear from the client reported that the client ended the session.</summary>
    public const string UnexpectedPeerEnded = "unexpected_peer_ended";

    /// <summary>Internal: the pipeline stopped while the session still needed it.</summary>
    public const string PipelineStoppedUnexpectedly = "pipeline_stopped_unexpectedly";

    /// <summary>Connection: a connection ended as lost without saying why.</summary>
    public const string ConnectionLostWithoutReason = "connection_lost";
}

/// <summary>How a fault becomes what the user is told (architecture section 9.3).</summary>
public static class SessionErrors
{
    /// <summary>The user-facing summary of <paramref name="fault"/>, with the fault kept as the cause.</summary>
    /// <param name="hasStreamed">Whether the session had streamed: a connection that never came up is "unavailable", one that went away is "lost".</param>
    /// <param name="detail">A sentence to add; by default the fault's own message when the user can act on it.</param>
    public static SessionError From(SessionFault fault, bool hasStreamed, string? detail = null)
    {
        SessionErrorCode code = fault.Category switch
        {
            FaultCategory.Transport => SessionErrorCode.TransportUnavailable,
            FaultCategory.Connection or FaultCategory.Timeout =>
                hasStreamed ? SessionErrorCode.ConnectionLost : SessionErrorCode.TransportUnavailable,
            FaultCategory.Protocol => fault.Code switch
            {
                SessionFaultCodes.NoCommonConfiguration or SessionFaultCodes.StreamConfigRejected => SessionErrorCode.NegotiationFailed,
                SessionFaultCodes.NotPaired or SessionFaultCodes.AuthenticationFailed => SessionErrorCode.AuthenticationFailed,
                _ => SessionErrorCode.IncompatibleProtocol,
            },
            FaultCategory.Capture => SessionErrorCode.CaptureUnavailable,
            FaultCategory.Encoder => SessionErrorCode.EncoderUnavailable,
            _ => SessionErrorCode.Internal,
        };

        return new SessionError(code, detail ?? (fault.UserActionable ? fault.Message : null), fault);
    }
}
