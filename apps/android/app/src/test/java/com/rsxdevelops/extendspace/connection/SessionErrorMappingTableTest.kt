package com.rsxdevelops.extendspace.connection

import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Runs `tests/session-faults/error-mapping.tsv`, at the repository root, against the Android
 * mapping. The Windows suite runs the same file against its own.
 */
class SessionErrorMappingTableTest {
    private data class Line(val category: FaultCategory, val code: String, val hasStreamed: Boolean?, val error: ConnectionErrorCode, val note: String)

    @Test
    fun `every category and every raised code of this platform is in the shared table`() {
        val lines = load()

        assertEquals(FaultCategory.entries.sorted(), lines.map { it.category }.distinct().sorted())

        // Nothing raises AUTHENTICATION_FAILED until pairing exists.
        assertEquals(
            ConnectionErrorCode.entries.filter { it != ConnectionErrorCode.AUTHENTICATION_FAILED }.sorted(),
            lines.map { it.error }.distinct().sorted(),
        )
    }

    @Test
    fun `every fault maps as the shared table says and is kept as the cause`() {
        val failures = load().flatMap { line ->
            (line.hasStreamed?.let { listOf(it) } ?: listOf(false, true)).mapNotNull { hasStreamed ->
                val fault = SessionFault(line.category, line.code, "Something.", FaultTreatment.TERMINAL)
                val error = SessionErrors.from(fault, hasStreamed)
                assertSame(fault, error.cause)
                if (error.code == line.error) null else "${line.category} ${line.code} (has streamed: $hasStreamed) maps to ${error.code}, the table says ${line.error}: ${line.note}"
            }
        }

        assertTrue("${failures.size} mapping(s) differ:\n" + failures.joinToString("\n"), failures.isEmpty())
    }

    @Test
    fun `the fault message is shown only when the user can act on it`() {
        val actionable = SessionFault(FaultCategory.TRANSPORT, "cannot_listen", "Close the other app using the socket.", FaultTreatment.TERMINAL, userActionable = true)

        assertEquals("Close the other app using the socket.", SessionErrors.from(actionable, false).detail)
        assertEquals(null, SessionErrors.from(actionable.copy(userActionable = false), false).detail)
        assertEquals("Given.", SessionErrors.from(actionable, false, "Given.").detail)
    }

    private fun load(): List<Line> {
        val lines = find().readLines().filter { it.isNotEmpty() && !it.startsWith("#") }
        assertEquals("category\tcode\thas_streamed\terror\tplatforms\tnote", lines.first())
        return lines.drop(1).map { it.split('\t') }.mapNotNull { fields ->
            assertEquals("fields in: $fields", 6, fields.size)
            if (fields[4] == "windows") return@mapNotNull null
            assertTrue("platforms must be both, windows or android: ${fields[4]}", fields[4] == "both" || fields[4] == "android")
            Line(
                category = FaultCategory.valueOf(fields[0].uppercase()),
                code = fields[1],
                hasStreamed = when (fields[2]) {
                    "yes" -> true
                    "no" -> false
                    "-" -> null
                    else -> error("has_streamed must be yes, no or -: ${fields[2]}")
                },
                error = ConnectionErrorCode.valueOf(fields[3].uppercase()),
                note = fields[5],
            )
        }
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
        const val RELATIVE_PATH = "tests/session-faults/error-mapping.tsv"
    }
}
