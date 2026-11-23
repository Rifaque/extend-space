package com.rsxdevelops.extendspace.connection

/**
 * Fault codes the session controller itself raises or acts on, and the protocol's own error codes
 * (protocol section 12) where a fault carries one. Components choose their own codes otherwise.
 * The same strings as the Windows `SessionFaultCodes`.
 */
internal object SessionFaultCodes {
    const val PROTOCOL_VERSION_UNSUPPORTED = "protocol_version_unsupported"
    const val NO_COMMON_CONFIGURATION = "no_common_configuration"
    const val STREAM_CONFIG_REJECTED = "stream_config_rejected"
    const val TEARDOWN_STEP_OVERRAN = "teardown_step_overran"
    const val TEARDOWN_STEP_FAILED = "teardown_step_failed"
    const val UNREQUESTED_CANCELLATION = "unrequested_cancellation"
    const val CONNECTION_LOST = "connection_lost"
}

/** How a fault becomes what the user is told: architecture section 9.3, the same table as Windows. */
internal object SessionErrors {
    /**
     * @param hasStreamed whether the session had streamed: a host that never came is "unavailable",
     * one that went away is "lost".
     * @param detail a sentence to add; by default the fault's own message when the user can act on it.
     */
    fun from(fault: SessionFault, hasStreamed: Boolean, detail: String? = null): ConnectionError {
        val code = when (fault.category) {
            FaultCategory.TRANSPORT -> ConnectionErrorCode.TRANSPORT_UNAVAILABLE
            FaultCategory.CONNECTION, FaultCategory.TIMEOUT ->
                if (hasStreamed) ConnectionErrorCode.CONNECTION_LOST else ConnectionErrorCode.TRANSPORT_UNAVAILABLE
            FaultCategory.PROTOCOL -> when (fault.code) {
                SessionFaultCodes.NO_COMMON_CONFIGURATION, SessionFaultCodes.STREAM_CONFIG_REJECTED -> ConnectionErrorCode.NEGOTIATION_FAILED
                else -> ConnectionErrorCode.INCOMPATIBLE_PROTOCOL
            }
            FaultCategory.DECODER -> ConnectionErrorCode.DECODER_UNAVAILABLE
            FaultCategory.CLEANUP, FaultCategory.INTERNAL -> ConnectionErrorCode.INTERNAL
        }
        return ConnectionError(code, detail ?: fault.message.takeIf { fault.userActionable }, fault)
    }
}
