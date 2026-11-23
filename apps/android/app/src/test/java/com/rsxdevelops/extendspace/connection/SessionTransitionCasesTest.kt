package com.rsxdevelops.extendspace.connection

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Runs the transition cases in `tests/session-transitions/cases.tsv`, at the repository root,
 * against the Android state machine. The Windows suite runs the same file against its own, so the
 * two cannot come to disagree about a transition without one of them failing.
 */
class SessionTransitionCasesTest {
    private data class Case(
        val id: String,
        val given: List<String>,
        val event: String,
        val accepted: Boolean,
        val phase: String,
        val hasStreamed: Boolean?,
        val attemptFaultKept: Boolean?,
        val note: String,
    )

    private val retryable = SessionFault(FaultCategory.CONNECTION, "host_closed_connection", "The host closed the connection.", FaultTreatment.RETRYABLE)
    private val terminal = SessionFault(FaultCategory.PROTOCOL, "protocol_version_unsupported", "The host speaks another protocol version.", FaultTreatment.TERMINAL)

    @Test
    fun `the shared table was found and covers what it must`() {
        val cases = load()

        assertTrue("only ${cases.size} cases were read", cases.size >= 50)
        assertEquals(cases.size, cases.map { it.id }.distinct().size)
        assertEquals(
            listOf(
                "acknowledge", "attempt_failed", "attempt_failed_terminal", "connect", "connection_lost",
                "connection_lost_terminal", "disconnect", "failed", "negotiation_completed", "teardown_completed", "transport_connected",
            ),
            cases.map { it.event }.distinct().sorted(),
        )
        assertEquals(
            listOf("Connecting", "Disconnected", "Disconnecting", "Failed", "Negotiating", "Reconnecting", "Streaming"),
            cases.map { it.phase }.distinct().sorted(),
        )
    }

    @Test
    fun `every case of the shared table holds for this state machine`() {
        val failures = load().mapNotNull { case ->
            try {
                run(case)
                null
            } catch (failure: AssertionError) {
                "${case.id} (${case.note}): ${failure.message}"
            }
        }

        assertTrue("${failures.size} case(s) failed:\n" + failures.joinToString("\n"), failures.isEmpty())
    }

    private fun run(case: Case) {
        val machine = ConnectionStateMachine()
        case.given.forEach { given ->
            assertTrue("the setup event '$given' was rejected in ${phaseOf(machine.state.value)}", machine.dispatch(event(given)))
        }

        val before = machine.state.value
        val accepted = machine.dispatch(event(case.event))
        val after = machine.state.value

        assertEquals("'${case.event}' in ${phaseOf(before)} accepted", case.accepted, accepted)
        assertEquals("phase", case.phase, phaseOf(after))

        // A rejected event must leave no trace at all.
        if (!case.accepted) assertEquals("state after a rejected event", before, after)

        // Where the table has '-', this platform's state cannot even express the notion: Disconnected,
        // Disconnecting and Failed carry neither. That is checked by hasStreamed() and attemptFault()
        // returning null for them.
        assertEquals("has streamed", case.hasStreamed, hasStreamed(after))
        assertEquals("attempt fault kept", case.attemptFaultKept, attemptFault(after)?.let { it.value != null })
        attemptFault(after)?.value?.let { assertEquals("the fault kept is the one that was reported", retryable, it) }
    }

    private fun event(name: String): ConnectionEvent = when (name) {
        "connect" -> ConnectionEvent.ConnectRequested(TransportKind.USB)
        "transport_connected" -> ConnectionEvent.TransportConnected
        "negotiation_completed" -> ConnectionEvent.NegotiationCompleted
        "connection_lost" -> ConnectionEvent.ConnectionLost(retryable)
        "connection_lost_terminal" -> ConnectionEvent.ConnectionLost(terminal)
        "attempt_failed" -> ConnectionEvent.AttemptFailed(retryable)
        "attempt_failed_terminal" -> ConnectionEvent.AttemptFailed(terminal)
        "disconnect" -> ConnectionEvent.DisconnectRequested
        "teardown_completed" -> ConnectionEvent.TeardownCompleted
        "failed" -> ConnectionEvent.Failed(ConnectionError(ConnectionErrorCode.CONNECTION_LOST, cause = retryable))
        "acknowledge" -> ConnectionEvent.ErrorAcknowledged
        else -> error("The shared table names an event this suite does not know: '$name'.")
    }

    private fun phaseOf(state: ConnectionState): String = when (state) {
        ConnectionState.Disconnected -> "Disconnected"
        is ConnectionState.Connecting -> "Connecting"
        is ConnectionState.Negotiating -> "Negotiating"
        is ConnectionState.Streaming -> "Streaming"
        is ConnectionState.Reconnecting -> "Reconnecting"
        ConnectionState.Disconnecting -> "Disconnecting"
        is ConnectionState.Failed -> "Failed"
    }

    /**
     * Whether the session has streamed. On Windows this is a field of the state; here it follows
     * from which state it is, except while negotiating, where it has to be carried.
     */
    private fun hasStreamed(state: ConnectionState): Boolean? = when (state) {
        is ConnectionState.Connecting -> false
        is ConnectionState.Negotiating -> state.resuming
        is ConnectionState.Streaming, is ConnectionState.Reconnecting -> true
        ConnectionState.Disconnected, ConnectionState.Disconnecting, is ConnectionState.Failed -> null
    }

    private class Held(val value: SessionFault?)

    private fun attemptFault(state: ConnectionState): Held? = when (state) {
        is ConnectionState.Connecting -> Held(state.lastAttemptFault)
        is ConnectionState.Negotiating -> Held(state.lastAttemptFault)
        is ConnectionState.Reconnecting -> Held(state.lastAttemptFault)
        is ConnectionState.Streaming -> Held(null)
        ConnectionState.Disconnected, ConnectionState.Disconnecting, is ConnectionState.Failed -> null
    }

    private fun load(): List<Case> {
        val lines = find().readLines().filter { it.isNotEmpty() && !it.startsWith("#") }
        assertEquals("id\tgiven\twhen\taccepted\tphase\thas_streamed\tattempt_fault\tnote", lines.first())
        return lines.drop(1).map { line ->
            val fields = line.split('\t')
            assertEquals("fields in: $line", 8, fields.size)
            Case(
                id = fields[0],
                given = if (fields[1] == "-") emptyList() else fields[1].split(','),
                event = fields[2],
                accepted = yesNo(fields[3]) ?: error("${fields[0]}: 'accepted' must be yes or no."),
                phase = fields[4],
                hasStreamed = yesNo(fields[5]),
                attemptFaultKept = when (fields[6]) {
                    "kept" -> true
                    "none" -> false
                    "-" -> null
                    else -> error("${fields[0]}: 'attempt_fault' must be kept, none or -.")
                },
                note = fields[7],
            )
        }
    }

    private fun yesNo(text: String): Boolean? = when (text) {
        "yes" -> true
        "no" -> false
        "-" -> null
        else -> error("Expected yes, no or -, read '$text'.")
    }

    private fun find(): File {
        var directory: File? = File("").absoluteFile
        while (directory != null) {
            val candidate = File(directory, RELATIVE_PATH)
            if (candidate.isFile) return candidate
            directory = directory.parentFile
        }
        error("'$RELATIVE_PATH' was not found in any directory above ${File("").absolutePath}.")
    }

    private companion object {
        const val RELATIVE_PATH = "tests/session-transitions/cases.tsv"
    }
}
