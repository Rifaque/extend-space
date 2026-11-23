package com.rsxdevelops.extendspace.connection

/**
 * Which part of a session a fault came from. The list is what the client can actually observe:
 * capture and encoding happen on the host and reach this side, if at all, as a [PROTOCOL] fault.
 */
enum class FaultCategory {
    /** The route to the host could not be set up: for USB, the local socket could not be listened on. */
    TRANSPORT,

    /** An accepted connection was refused, closed or reset. */
    CONNECTION,

    /** The peer broke the protocol, speaks another version, or offered a stream this device cannot take. */
    PROTOCOL,

    /** No decoder could be created, or the decoder failed while running. */
    DECODER,

    /** Something that had to happen within a time did not: a handshake step, a keepalive. */
    TIMEOUT,

    /** A teardown step failed or overran. Recorded; teardown carries on. */
    CLEANUP,

    /** Anything unexpected. Carries the exception's type and message. */
    INTERNAL,
}

/** What the session's owner may do about a fault. */
enum class FaultTreatment {
    /** The cause can change by itself, so waiting for another attempt is worth it while the budget lasts. */
    RETRYABLE,

    /** Trying again cannot help. The session ends with this fault. */
    TERMINAL,
}

/**
 * One thing that went wrong in a session, as the component that saw it reports it to the session's
 * owner. It says where, what and whether trying again can help, and nothing about how the link or
 * the decoder is implemented.
 *
 * Cancellation is not a fault and has no representation here: an operation that was cancelled
 * reports [AttemptOutcome.Cancelled].
 *
 * @param code stable, lower_snake_case, for logs and tests; for example `host_closed_connection`.
 * @param message a sentence for a log or a status line. Never video data or file paths.
 * @param userActionable true when the person holding the device can do something about it.
 */
data class SessionFault(
    val category: FaultCategory,
    val code: String,
    val message: String,
    val treatment: FaultTreatment,
    val userActionable: Boolean = false,
) {
    val isRetryable: Boolean get() = treatment == FaultTreatment.RETRYABLE

    companion object {
        /** An exception nothing expected, keeping its type and message so that the record says what happened. */
        fun unexpected(exception: Throwable): SessionFault = SessionFault(
            FaultCategory.INTERNAL,
            "unexpected_exception",
            "${exception.javaClass.simpleName}: ${exception.message}",
            FaultTreatment.TERMINAL,
        )
    }
}

/** How one step of a session attempt ended: exactly one of the three. */
sealed interface AttemptOutcome<out T> {
    data class Succeeded<T>(val value: T) : AttemptOutcome<T>

    data class Failed(val fault: SessionFault) : AttemptOutcome<Nothing>

    /** The caller's cancellation was observed. Nothing went wrong and nothing is to be reported as an error. */
    data object Cancelled : AttemptOutcome<Nothing>
}
