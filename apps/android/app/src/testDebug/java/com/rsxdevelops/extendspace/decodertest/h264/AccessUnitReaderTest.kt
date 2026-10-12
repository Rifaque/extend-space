package com.rsxdevelops.extendspace.decodertest.h264

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.InputStream

/**
 * The NAL payloads here are placeholders. Splitting a stream into pictures depends only on start
 * codes, the NAL header byte and the first bit of a slice header, so that is all they get right.
 */
class AccessUnitReaderTest {
    private val startCode4 = bytes(0, 0, 0, 1)
    private val startCode3 = bytes(0, 0, 1)

    private val aud = bytes(0x09, 0x10)
    private val sps = bytes(0x67, 100, 0x00, 51, 0xAC, 0x2B)
    private val pps = bytes(0x68, 0xEE, 0x3C, 0xB0)
    private val sei = bytes(0x06, 0x05, 0x01, 0xAA, 0x80)

    // The byte after the header starts the slice header: its top bit set means first_mb_in_slice = 0,
    // the first slice of a picture. Top bit clear means a later slice of the same picture.
    private val idrFirstSlice = bytes(0x65, 0x88, 0x84, 0x21, 0xA0)
    private val idrLaterSlice = bytes(0x65, 0x04, 0x21, 0x43, 0xA0)
    private val firstSlice = bytes(0x41, 0x9A, 0x24, 0x6C, 0x41)
    private val laterSlice = bytes(0x41, 0x05, 0x13, 0x22, 0x41)

    @Test
    fun `a picture with its parameter sets and one slice is one access unit`() {
        val stream = join(aud, sps, pps, sei, idrFirstSlice)

        val units = readAll(stream)

        assertEquals(1, units.size)
        assertArrayEquals(stream, units[0].data)
        assertEquals(listOf(9, 7, 8, 6, 5), units[0].nalUnits.map { it.type })
        assertTrue(units[0].isIdr)
        assertArrayEquals(sps, units[0].firstOfType(NalType.SPS))
        assertArrayEquals(pps, units[0].firstOfType(NalType.PPS))
    }

    @Test
    fun `the slices of one picture stay together`() {
        // Sixteen slices per picture is what Microsoft's software encoder produced at 2560 x 1600.
        val picture = arrayOf(aud, firstSlice) + Array(15) { laterSlice }
        val stream = join(*picture, *picture, *picture)

        val units = readAll(stream)

        assertEquals(3, units.size)
        units.forEach { unit ->
            assertEquals(16, unit.nalUnits.count { NalType.isVcl(it.type) })
            assertArrayEquals(join(*picture), unit.data)
        }
    }

    @Test
    fun `without delimiters a picture starts at the slice whose first macroblock is zero`() {
        val stream = join(idrFirstSlice, idrLaterSlice, firstSlice, laterSlice, laterSlice, firstSlice)

        val units = readAll(stream)

        assertEquals(listOf(2, 3, 1), units.map { it.nalUnits.size })
        assertEquals(listOf(true, false, false), units.map { it.isIdr })
        assertArrayEquals(join(firstSlice, laterSlice, laterSlice), units[1].data)
    }

    @Test
    fun `parameter sets and SEI ahead of a picture belong to that picture, not the one before`() {
        val stream = join(sps, pps, idrFirstSlice, firstSlice, sps, pps, sei, idrFirstSlice)

        val units = readAll(stream)

        assertEquals(3, units.size)
        assertEquals(listOf(7, 8, 5), units[0].nalUnits.map { it.type })
        assertEquals(listOf(1), units[1].nalUnits.map { it.type })
        assertEquals(listOf(7, 8, 6, 5), units[2].nalUnits.map { it.type })
        assertNull(units[1].firstOfType(NalType.SPS))
    }

    @Test
    fun `the pieces put back together are the original stream`() {
        val stream = join(aud, sps, pps, sei, idrFirstSlice, idrLaterSlice, aud, firstSlice, laterSlice, aud, firstSlice)

        val rebuilt = readAll(stream).fold(ByteArray(0)) { all, unit -> all + unit.data }

        assertArrayEquals(stream, rebuilt)
    }

    @Test
    fun `three-byte and four-byte start codes can be mixed`() {
        val stream = startCode3 + aud + startCode4 + sps + startCode3 + pps + startCode3 + idrFirstSlice +
            startCode4 + aud + startCode3 + firstSlice

        val units = readAll(stream)

        assertEquals(2, units.size)
        assertArrayEquals(startCode3 + aud + startCode4 + sps + startCode3 + pps + startCode3 + idrFirstSlice, units[0].data)
        assertArrayEquals(startCode4 + aud + startCode3 + firstSlice, units[1].data)
    }

    @Test
    fun `how the bytes arrive does not change the result`() {
        val picture = arrayOf(aud, sps, pps, idrFirstSlice, idrLaterSlice)
        val stream = join(*picture, aud, firstSlice, laterSlice, aud, firstSlice, laterSlice, laterSlice)
        val expected = readAll(stream).map { it.data.toList() }
        assertEquals(3, expected.size)

        for (chunk in listOf(1, 2, 3, 4, 5, 7, 13, 64)) {
            val units = readAll(TricklingStream(stream, chunk))
            assertEquals("in chunks of $chunk", expected, units.map { it.data.toList() })
        }
    }

    @Test
    fun `nal unit positions are relative to the access unit they are in`() {
        val units = readAll(join(aud, sps, pps, idrFirstSlice, aud, firstSlice))

        val second = units[1]
        assertEquals(0, second.nalUnits[0].start)
        assertArrayEquals(aud, second.data.copyOfRange(second.nalUnits[0].payload, second.nalUnits[0].end))
        assertArrayEquals(firstSlice, second.data.copyOfRange(second.nalUnits[1].payload, second.nalUnits[1].end))
    }

    @Test
    fun `zero padding before the first start code and after the last picture is tolerated`() {
        val units = readAll(bytes(0, 0, 0) + join(aud, sps, pps, idrFirstSlice) + bytes(0, 0, 0, 0, 0))

        assertEquals(1, units.size)
        assertEquals(idrFirstSlice.toList(), units[0].data.takeLast(idrFirstSlice.size))
    }

    @Test
    fun `reading past the end keeps returning null`() {
        val reader = AccessUnitReader(ByteArrayInputStream(join(sps, pps, idrFirstSlice)))

        assertEquals(1, generateSequence { reader.next() }.count())
        assertNull(reader.next())
        assertNull(reader.next())
        assertEquals(1, reader.accessUnitsRead)
    }

    @Test
    fun `parameter sets with no picture are not an access unit`() {
        val reader = AccessUnitReader(ByteArrayInputStream(join(sps, pps)))

        assertNull(reader.next())
        assertEquals(0, reader.accessUnitsRead)
    }

    @Test
    fun `an empty file is reported as such`() {
        val exception = assertThrows(MalformedStreamException::class.java) {
            AccessUnitReader(ByteArrayInputStream(ByteArray(0))).next()
        }
        assertTrue(exception.message!!.contains("empty"))
    }

    @Test
    fun `a file that is not Annex B is rejected rather than guessed at`() {
        // Length-prefixed NAL units, as stored inside an MP4 file.
        val avcc = bytes(0, 0, 0, 5) + idrFirstSlice + bytes(0, 0, 0, 5) + firstSlice
        assertThrows(MalformedStreamException::class.java) { readAll(avcc) }

        // An MP4 header in front of what is otherwise a valid stream.
        val withHeader = "ftypisom".toByteArray() + join(sps, pps, idrFirstSlice)
        assertThrows(MalformedStreamException::class.java) { readAll(withHeader) }
    }

    @Test
    fun `data with no picture boundary is given up on at the size limit`() {
        val endless = startCode4 + bytes(0x41, 0x9A) + ByteArray(8_000) { 0x55 }

        assertThrows(MalformedStreamException::class.java) {
            AccessUnitReader(TricklingStream(endless, 100), maxAccessUnitBytes = 1_024).next()
        }
    }

    @Test
    fun `a picture larger than the initial buffer is read whole`() {
        val bigSlice = bytes(0x65, 0x88) + ByteArray(700_000) { (it % 251 + 1).toByte() }
        val stream = join(sps, pps, bigSlice, firstSlice)

        val units = readAll(TricklingStream(stream, 60_000))

        assertEquals(2, units.size)
        assertArrayEquals(join(sps, pps, bigSlice), units[0].data)
        assertFalse(units[1].isIdr)
    }

    @Test
    fun `the start code search finds codes wherever they fall`() {
        for (prefix in 0..6) {
            val data = ByteArray(prefix) { 0x7F } + bytes(0, 0, 1, 0x65)
            assertEquals("after $prefix other bytes", prefix, AnnexB.indexOfStartCode(data, 0, data.size))
        }
        assertEquals(-1, AnnexB.indexOfStartCode(bytes(0, 0, 2, 0, 1, 0, 0), 0, 7))
        assertEquals(-1, AnnexB.indexOfStartCode(bytes(0, 0, 1), 0, 2))
        assertEquals(1, AnnexB.indexOfStartCode(bytes(0, 0, 0, 1), 0, 4))
    }

    @Test
    fun `a whole buffer splits into nal units without their start codes or padding`() {
        val data = join(sps, pps) + bytes(0, 0) + startCode3 + idrFirstSlice + bytes(0)

        val units = AnnexB.findNalUnits(data)

        assertEquals(listOf(7, 8, 5), units.map { it.type })
        assertArrayEquals(sps, data.copyOfRange(units[0].payload, units[0].end))
        assertArrayEquals(pps, data.copyOfRange(units[1].payload, units[1].end))
        assertArrayEquals(idrFirstSlice, data.copyOfRange(units[2].payload, units[2].end))
    }

    private fun readAll(stream: ByteArray): List<AccessUnit> = readAll(ByteArrayInputStream(stream))

    private fun readAll(input: InputStream): List<AccessUnit> {
        val reader = AccessUnitReader(input)
        return generateSequence { reader.next() }.toList()
    }

    private fun join(vararg nalUnits: ByteArray): ByteArray =
        nalUnits.fold(ByteArray(0)) { all, unit -> all + startCode4 + unit }

    private fun bytes(vararg values: Int): ByteArray = ByteArray(values.size) { values[it].toByte() }

    /** Hands out at most [chunk] bytes per read, as a slow or fragmented source would. */
    private class TricklingStream(private val data: ByteArray, private val chunk: Int) : InputStream() {
        private var position = 0

        override fun read(): Int = if (position < data.size) data[position++].toInt() and 0xFF else -1

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            if (position >= data.size) return -1
            val count = minOf(chunk, length, data.size - position)
            System.arraycopy(data, position, buffer, offset, count)
            position += count
            return count
        }
    }
}
