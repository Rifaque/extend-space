package com.rsxdevelops.extendspace.ui.session

import androidx.annotation.StringRes
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionErrorCode
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.TransportKind

/** What the user can do from the session screen when it is not showing the picture. */
enum class SessionAction {
    /** Stop waiting, or stop reconnecting: ends the session. */
    CANCEL,

    /** After a session ended: wait for the PC again. */
    WAIT_AGAIN,

    /** After a failure: clear it and wait for the PC again. */
    TRY_AGAIN,

    /** Leave the session screen. */
    CLOSE,
}

/**
 * What the session screen shows over (or instead of) the picture for a session state (D66). Pure, so
 * that the mapping is tested on the JVM; the screen only draws it. Null while streaming: the picture
 * fills the screen and nothing covers it.
 */
data class SessionOverlay(
    @StringRes val title: Int,
    @StringRes val detail: Int,
    val actions: List<SessionAction>,
    val showsProgress: Boolean,
)

fun sessionOverlay(state: ConnectionState): SessionOverlay? = when (state) {
    is ConnectionState.Streaming -> null
    is ConnectionState.Connecting ->
        SessionOverlay(
            R.string.session_waiting_title,
            if (state.transport == TransportKind.WIRELESS) R.string.session_waiting_detail_wifi else R.string.session_waiting_detail,
            listOf(SessionAction.CANCEL),
            showsProgress = true,
        )
    is ConnectionState.Negotiating ->
        SessionOverlay(R.string.session_starting_title, R.string.session_starting_detail, listOf(SessionAction.CANCEL), showsProgress = true)
    is ConnectionState.Reconnecting ->
        SessionOverlay(
            R.string.session_reconnecting_title,
            if (state.transport == TransportKind.WIRELESS) R.string.session_reconnecting_detail_wifi else R.string.session_reconnecting_detail,
            listOf(SessionAction.CANCEL),
            showsProgress = true,
        )
    ConnectionState.Disconnecting ->
        SessionOverlay(R.string.session_ending_title, R.string.session_ending_detail, emptyList(), showsProgress = true)
    ConnectionState.Disconnected ->
        SessionOverlay(R.string.session_ended_title, R.string.session_ended_detail, listOf(SessionAction.WAIT_AGAIN, SessionAction.CLOSE), showsProgress = false)
    is ConnectionState.Failed ->
        SessionOverlay(R.string.state_failed, state.error.code.message(), listOf(SessionAction.TRY_AGAIN, SessionAction.CLOSE), showsProgress = false)
}

/** True while a session holds or is about to hold the link: the states in which leaving the screen should end it. */
val ConnectionState.isActive: Boolean
    get() = this is ConnectionState.Connecting ||
        this is ConnectionState.Negotiating ||
        this is ConnectionState.Streaming ||
        this is ConnectionState.Reconnecting

/** The sentence the user is shown for a failure. Shared by the Connect and session screens. */
@StringRes
fun ConnectionErrorCode.message(): Int = when (this) {
    ConnectionErrorCode.TRANSPORT_UNAVAILABLE -> R.string.error_transport_unavailable
    ConnectionErrorCode.AUTHENTICATION_FAILED -> R.string.error_authentication_failed
    ConnectionErrorCode.INCOMPATIBLE_PROTOCOL -> R.string.error_incompatible_protocol
    ConnectionErrorCode.NEGOTIATION_FAILED -> R.string.error_negotiation_failed
    ConnectionErrorCode.CONNECTION_LOST -> R.string.error_connection_lost
    ConnectionErrorCode.DECODER_UNAVAILABLE -> R.string.error_decoder_unavailable
    ConnectionErrorCode.INTERNAL -> R.string.error_internal
}
