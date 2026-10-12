package com.rsxdevelops.extendspace.decodertest.h264

import android.media.MediaCodecInfo.CodecProfileLevel
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The parameter sets below are real: each was copied out of a file written by the Windows capture
 * experiment, and the expected values are what ffprobe reports for that same file. They contain
 * stream parameters only, no picture data.
 */
class SequenceParameterSetTest {
    private class Sample(
        val encoder: String,
        val hex: String,
        val width: Int,
        val height: Int,
        val profile: String,
        val level: String,
        val framesPerSecond: Double,
        val fullRange: Boolean?,
    )

    private val samples = listOf(
        Sample("Intel Quick Sync", "27640033ac2b60140064d80a04000003000400000301e3a10002dc600002dc6c6f7be0ed0e1970", 2560, 1600, "High", "5.1", 60.0, false),
        Sample("Intel Quick Sync, 165 fps", "27640034ac2b60140064d80a0400000300040000052ba10002dc600002dc6c6f7be0ed0e1970", 2560, 1600, "High", "5.2", 165.0, false),
        Sample("Microsoft software", "67640033ac2b40140064d80880000003008000003c06d0442350", 2560, 1600, "High", "5.1", 60.0, null),
        Sample("Microsoft software", "6764002aac2b403c0113f2e022000003000200000300f01b41108d40", 1920, 1080, "High", "4.2", 60.0, null),
        Sample("NVIDIA", "67640033ac2b4014005ad808800001f40000ea60078e1550", 2560, 1440, "High", "5.1", 60.0, null),
        Sample("NVIDIA", "6764002aac2b403c0113f2e022000007d00003a9801e385540", 1920, 1080, "High", "4.2", 60.0, null),
        Sample("NVIDIA", "6764002aac2b402b030f37808800001f40000ea60078e155", 1366, 768, "High", "4.2", 60.0, null),
        Sample("NVIDIA, Main, 30 fps", "674d401f95a014016ec04400000fa00003a9803c70aa80", 1280, 720, "Main", "3.1", 30.0, null),
        Sample("NVIDIA, Baseline", "6742c01f95a0360f79bc04400000fa0000753003c70aa8", 854, 480, "Constrained Baseline", "3.1", 60.0, null),
    )

    @Test
    fun `real parameter sets parse to what ffprobe reports`() {
        for (sample in samples) {
            val label = "${sample.encoder} ${sample.width}x${sample.height}"
            val sps = SequenceParameterSet.parse(hex(sample.hex))

            assertEquals(label, sample.width, sps.width)
            assertEquals(label, sample.height, sps.height)
            assertEquals(label, sample.profile, sps.profileName)
            assertEquals(label, sample.level, sps.levelName)
            assertEquals(label, sample.framesPerSecond, sps.framesPerSecond!!, 0.001)
            assertEquals(label, sample.fullRange, sps.fullRange)
            assertFalse(label, sps.interlaced)
            assertTrue(label, sps.maxReferenceFrames in 1..2)
            assertNull("$label: none of these encoders writes the colour matrix", sps.matrixCoefficients)
        }
    }

    @Test
    fun `sizes that are not whole macroblocks come from the cropping fields`() {
        // 1080 is 67.5 macroblocks high, 1366 is 85.375 wide and 854 is 53.375 wide.
        assertEquals(1080, SequenceParameterSet.parse(hex(samples[5].hex)).height)
        assertEquals(1366, SequenceParameterSet.parse(hex(samples[6].hex)).width)
        assertEquals(854, SequenceParameterSet.parse(hex(samples[8].hex)).width)
    }

    @Test
    fun `emulation prevention bytes are removed before the fields are read`() {
        assertArrayEquals(
            bytes(0x67, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x04),
            bytes(0x67) + SequenceParameterSet.removeEmulationPrevention(
                bytes(0x67, 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x00, 0x03, 0x04),
                from = 1,
            ),
        )
        // The Intel parameter set only yields 60 fps if its two escaped zero runs are undone.
        assertTrue(samples[0].hex.contains("000003"))
    }

    @Test
    fun `something that is not a sequence parameter set is refused`() {
        assertThrows(MalformedStreamException::class.java) { SequenceParameterSet.parse(hex("68ee3cb0")) }
        assertThrows(MalformedStreamException::class.java) { SequenceParameterSet.parse(ByteArray(0)) }
    }

    @Test
    fun `a truncated parameter set fails cleanly at any length`() {
        val whole = hex(samples[0].hex)
        // Everything up to the frame-size fields is needed; stopping anywhere before is an error.
        for (length in 1..12) {
            assertThrows("cut to $length bytes", MalformedStreamException::class.java) {
                SequenceParameterSet.parse(whole.copyOf(length))
            }
        }
    }

    @Test
    fun `exp-golomb codes decode as the standard defines them`() {
        // ue(v): 1 -> 0, 010 -> 1, 011 -> 2, 00100 -> 3, 00101 -> 4, 00110 -> 5, 00111 -> 6
        val unsigned = BitReader(bits("1" + "010" + "011" + "00100" + "00101" + "00110" + "00111"))
        assertEquals((0..6).toList(), List(7) { unsigned.readUnsignedExpGolomb() })

        // se(v): codes 0, 1, 2, 3, 4 mean 0, 1, -1, 2, -2
        val signed = BitReader(bits("1" + "010" + "011" + "00100" + "00101"))
        assertEquals(listOf(0, 1, -1, 2, -2), List(5) { signed.readSignedExpGolomb() })
    }

    @Test
    fun `reading past the end of the bits is an error, not garbage`() {
        val reader = BitReader(bytes(0xFF))
        assertEquals(0xFF, reader.readBits(8))
        assertThrows(MalformedStreamException::class.java) { reader.readBit() }
        assertThrows(MalformedStreamException::class.java) { BitReader(bytes(0x00, 0x00)).readUnsignedExpGolomb() }
    }

    @Test
    fun `profiles and levels map onto Android's constants`() {
        val high51 = SequenceParameterSet.parse(hex(samples[0].hex))
        assertEquals(CodecProfileLevel.AVCProfileHigh, AvcProfileLevel.profile(high51))
        assertEquals(CodecProfileLevel.AVCLevel51, AvcProfileLevel.level(high51))

        val main31 = SequenceParameterSet.parse(hex(samples[7].hex))
        assertEquals(CodecProfileLevel.AVCProfileMain, AvcProfileLevel.profile(main31))
        assertEquals(CodecProfileLevel.AVCLevel31, AvcProfileLevel.level(main31))

        val constrainedBaseline = SequenceParameterSet.parse(hex(samples[8].hex))
        assertEquals(CodecProfileLevel.AVCProfileConstrainedBaseline, AvcProfileLevel.profile(constrainedBaseline))

        assertEquals(CodecProfileLevel.AVCProfileBaseline, AvcProfileLevel.profile(high51.copy(profileIdc = 66, constraintFlags = 0)))
        assertEquals(CodecProfileLevel.AVCProfileConstrainedHigh, AvcProfileLevel.profile(high51.copy(constraintFlags = 0x0C)))
        assertNull(AvcProfileLevel.profile(high51.copy(profileIdc = 1)))
        assertNull(AvcProfileLevel.level(high51.copy(levelIdc = 99)))
    }

    @Test
    fun `a decoder covers a stream only at the same profile and at least its level`() {
        val high51 = SequenceParameterSet.parse(hex(samples[0].hex))
        val high = CodecProfileLevel.AVCProfileHigh
        val main = CodecProfileLevel.AVCProfileMain

        assertTrue(AvcProfileLevel.isCovered(high51, listOf(high to CodecProfileLevel.AVCLevel52)))
        assertTrue(AvcProfileLevel.isCovered(high51, listOf(main to CodecProfileLevel.AVCLevel3, high to CodecProfileLevel.AVCLevel51)))
        assertFalse(AvcProfileLevel.isCovered(high51, listOf(high to CodecProfileLevel.AVCLevel5)))
        assertFalse(AvcProfileLevel.isCovered(high51, listOf(main to CodecProfileLevel.AVCLevel62)))
        assertFalse(AvcProfileLevel.isCovered(high51, emptyList()))
        assertFalse(AvcProfileLevel.isCovered(high51.copy(profileIdc = 1), listOf(high to CodecProfileLevel.AVCLevel62)))
    }

    @Test
    fun `level and profile names read the way people write them`() {
        assertEquals("5.1", AvcProfileLevel.levelName(CodecProfileLevel.AVCLevel51))
        assertEquals("4", AvcProfileLevel.levelName(CodecProfileLevel.AVCLevel4))
        assertEquals("Constrained High", AvcProfileLevel.profileName(CodecProfileLevel.AVCProfileConstrainedHigh))
        assertEquals("4", SequenceParameterSet.parse(hex(samples[0].hex)).copy(levelIdc = 40).levelName)
    }

    private fun hex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private fun bytes(vararg values: Int): ByteArray = ByteArray(values.size) { values[it].toByte() }

    /** Packs a string of 0s and 1s into bytes, padding the last byte with zeros. */
    private fun bits(text: String): ByteArray {
        val padded = text.padEnd((text.length + 7) / 8 * 8, '0')
        return ByteArray(padded.length / 8) { padded.substring(it * 8, it * 8 + 8).toInt(2).toByte() }
    }
}
