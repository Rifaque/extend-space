package com.rsxdevelops.extendspace.decodertest

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Covers the arithmetic and wording only. Decoding itself is verified on a device, not here. */
class DecodeStatusTest {
    @Test
    fun `frames per second is frames over elapsed time`() {
        val status = DecodeStatus(framesDecoded = 300, elapsedNanos = 5_000_000_000)
        assertEquals(60.0, status.framesPerSecond!!, 1e-9)
    }

    @Test
    fun `no rate is reported before there is a frame or any elapsed time`() {
        assertNull(DecodeStatus().framesPerSecond)
        assertNull(DecodeStatus(framesDecoded = 10, elapsedNanos = 0).framesPerSecond)
        assertNull(DecodeStatus(framesDecoded = 0, elapsedNanos = 1_000_000).framesPerSecond)
    }

    @Test
    fun `only preparing and decoding count as running`() {
        assertEquals(
            setOf(DecodeState.Preparing, DecodeState.Decoding),
            DecodeState.entries.filter { DecodeStatus(state = it).isRunning }.toSet(),
        )
    }

    @Test
    fun `the summary states the measured counts and says how frames were paced`() {
        val summary = DecodeSummary.format(
            DecodeStatus(
                state = DecodeState.Completed,
                sampleName = "sample.h264",
                sampleBytes = 1_234_567,
                options = DecodeOptions(paced = false),
                decoderName = "c2.example.avc.decoder",
                decoderKind = "hardware, vendor",
                accessUnitsQueued = 600,
                keyFramesQueued = 5,
                bytesQueued = 1_234_567,
                framesDecoded = 600,
                framesReportedDisplayed = 598,
                elapsedNanos = 2_500_000_000,
                timeToFirstFrameNanos = 41_500_000,
            ),
        )

        assertTrue(summary.contains("state: Completed"))
        assertTrue(summary.contains("file: sample.h264 (1234567 bytes)"))
        assertTrue(summary.contains("decoder: c2.example.avc.decoder [hardware, vendor]"))
        assertTrue(summary.contains("pictures queued: 600 (5 keyframes, 1234567 bytes)"))
        assertTrue(summary.contains("frames decoded and released to the surface: 600"))
        assertTrue(summary.contains("frames the system reported as displayed: 598"))
        assertTrue(summary.contains("elapsed: 2500.0 ms"))
        assertTrue(summary.contains("time to first frame: 41.5 ms"))
        assertTrue(summary.contains("frames per second: 240.00"))
        assertTrue(summary.contains("pacing: none"))
        assertTrue(summary.contains("error: none"))
    }

    @Test
    fun `a run that never got going reports nothing as measured`() {
        val summary = DecodeSummary.format(DecodeStatus(state = DecodeState.Failed, error = "The file is empty."))

        assertTrue(summary.contains("stream: not read"))
        assertTrue(summary.contains("decoder: none"))
        assertTrue(summary.contains("frames per second: n/a"))
        assertTrue(summary.contains("time to first frame: n/a"))
        assertTrue(summary.contains("error: The file is empty."))
        assertFalse(summary.contains("null"))
    }
}
