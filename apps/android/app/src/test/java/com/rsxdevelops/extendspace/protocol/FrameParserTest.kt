package com.rsxdevelops.extendspace.protocol

// Copied from the debug experiment (testDebug/.../livestream/protocol/FrameParserTest.kt)
// for the production link and receiver, package changed only (migration stage 5, decision D52).
// The debug copy stays until the experiment is retired (stage 8).

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class FrameParserTest {
    private class Parsed(val header: FrameHeader, val payload: ByteArray)

    @Test
    fun `a stream that arrives whole yields its frames in order`() {
        val stream = Protocol.PREFACE + frame(0, 1, 0, bytes(1, 2, 3)) + frame(1, 0, 3, pattern(40)) + frame(0, 10, 0, ByteArray(0))

        val frames = parse(stream, stream.size)

        assertEquals(3, frames.size)
        assertEquals(FrameHeader(0, 1, 0, 3), frames[0].header)
        assertArrayEquals(bytes(1, 2, 3), frames[0].payload)
        assertEquals(FrameHeader(1, 0, 3, 40), frames[1].header)
        assertArrayEquals(pattern(40), frames[1].payload)
        assertEquals(FrameHeader(0, 10, 0, 0), frames[2].header)
        assertEquals(0, frames[2].payload.size)
    }

    @Test
    fun `however the bytes are cut up the frames come out the same`() {
        val stream = Protocol.PREFACE +
            frame(0, 1, 0, pattern(17)) +
            frame(1, 0, 1, pattern(300)) +
            frame(1, 0, 0, pattern(1)) +
            frame(0, 6, 0, ByteArray(0)) +
            frame(1, 0, 2, pattern(4096))
        val expected = parse(stream, stream.size)
        assertEquals(5, expected.size)

        for (chunk in listOf(1, 2, 3, 5, 7, 8, 9, 13, 64, 1000)) {
            val actual = parse(stream, chunk)
            assertEquals("in chunks of $chunk", expected.map { it.header }, actual.map { it.header })
            assertEquals("in chunks of $chunk", expected.map { it.payload.toList() }, actual.map { it.payload.toList() })
        }
    }

    @Test
    fun `several messages in one read are all delivered`() {
        val parser = FrameParser()
        val types = ArrayList<Int>()
        val oneRead = Protocol.PREFACE + frame(0, 6, 0, bytes(1)) + frame(0, 7, 0, bytes(2)) + frame(0, 8, 0, bytes(3)) + frame(0, 5, 0, bytes(4))

        parser.append(oneRead, 0, oneRead.size) { header, _, _ -> types += header.type }

        assertEquals(listOf(6, 7, 8, 5), types)
        assertEquals(4, parser.framesParsed)
        assertTrue(parser.isAtFrameBoundary)
    }

    @Test
    fun `only the stated part of the input is read`() {
        val parser = FrameParser()
        val stream = Protocol.PREFACE + frame(0, 6, 0, bytes(9))
        val padded = bytes(0x55, 0x55) + stream + bytes(0x55, 0x55, 0x55)
        var delivered = 0

        parser.append(padded, 2, stream.size) { _, payload, length ->
            assertEquals(1, length)
            assertEquals(9, payload[0].toInt())
            delivered++
        }

        assertEquals(1, delivered)
    }

    @Test
    fun `a stream that stops short is recognised as truncated`() {
        // Preface (5), header (8), ten payload bytes: anything shorter than all 23 is incomplete.
        val whole = Protocol.PREFACE + frame(0, 1, 0, pattern(10))

        for (keep in listOf(0, 3, 6, 12, 13, 20, 22)) {
            val parser = FrameParser()
            var delivered = 0
            parser.append(whole, 0, keep) { _, _, _ -> delivered++ }

            assertEquals("after $keep bytes", 0, delivered)
            assertFalse("after $keep bytes", parser.isAtFrameBoundary)
            assertThrows("after $keep bytes", ProtocolException::class.java) { parser.complete() }
        }

        // Exactly the preface, or the whole thing, are clean places to stop.
        FrameParser().apply { append(whole, 0, 5) { _, _, _ -> } }.complete()
        FrameParser().apply { append(whole, 0, whole.size) { _, _, _ -> } }.complete()
    }

    @Test
    fun `anything but the preface is rejected at the first wrong byte`() {
        for (opening in listOf("HTTP/".toByteArray(), bytes('E'.code, 'X'.code, 'S'.code, 'P'.code, 1), bytes('E'.code, 'X'.code, 'S'.code, 'Q'.code), bytes(0))) {
            val parser = FrameParser()

            val exception = assertThrows(ProtocolException::class.java) { parser.append(opening, 0, opening.size) { _, _, _ -> } }

            assertEquals(ErrorCode.MALFORMED_MESSAGE, exception.code)
            assertFalse(parser.prefaceReceived)
        }
    }

    @Test
    fun `a frame at its channel limit is accepted and one byte more is not`() {
        val limits = mapOf(
            Protocol.CHANNEL_CONTROL to Protocol.MAX_CONTROL_PAYLOAD,
            Protocol.CHANNEL_VIDEO to Protocol.MAX_VIDEO_PAYLOAD,
            Protocol.CHANNEL_INPUT to Protocol.MAX_INPUT_PAYLOAD,
        )
        for ((channel, limit) in limits) {
            val atLimit = FrameParser()
            var deliveredLength = -1
            val opening = Protocol.PREFACE + header(channel, 1, 0, limit.toLong())
            atLimit.append(opening, 0, opening.size) { _, _, _ -> }
            atLimit.append(ByteArray(limit), 0, limit) { _, _, length -> deliveredLength = length }
            assertEquals(limit, deliveredLength)

            val tooLong = Protocol.PREFACE + header(channel, 1, 0, limit + 1L)
            val exception = assertThrows(ProtocolException::class.java) { FrameParser().append(tooLong, 0, tooLong.size) { _, _, _ -> } }
            assertEquals(ErrorCode.MESSAGE_TOO_LARGE, exception.code)
        }
    }

    @Test
    fun `an absurd length is refused from the header alone on every channel`() {
        for (length in listOf(0xFFFFFFFFL, 0x80000000L, 0x7FFFFFFFL)) {
            for (channel in listOf(0, 1, 2, 3, 200)) {
                val opening = Protocol.PREFACE + header(channel, 0, 0, length)

                val exception = assertThrows("channel $channel, length $length", ProtocolException::class.java) {
                    FrameParser().append(opening, 0, opening.size) { _, _, _ -> }
                }

                assertEquals(ErrorCode.MESSAGE_TOO_LARGE, exception.code)
            }
        }
    }

    @Test
    fun `the negotiated video limit can only lower the default`() {
        val overLowered = Protocol.PREFACE + header(Protocol.CHANNEL_VIDEO, 0, 0, 1001)
        assertThrows(ProtocolException::class.java) { FrameParser(maxVideoPayload = 1000).append(overLowered, 0, overLowered.size) { _, _, _ -> } }

        val overDefault = Protocol.PREFACE + header(Protocol.CHANNEL_VIDEO, 0, 0, Protocol.MAX_VIDEO_PAYLOAD + 1L)
        assertThrows(ProtocolException::class.java) { FrameParser(maxVideoPayload = Int.MAX_VALUE).append(overDefault, 0, overDefault.size) { _, _, _ -> } }
    }

    @Test
    fun `frames on an unknown channel are skipped and what follows is still read`() {
        val stream = Protocol.PREFACE +
            frame(9, 77, 0xFFFF, pattern(5000)) +
            frame(0, 1, 0, bytes(42)) +
            frame(250, 1, 0, ByteArray(0)) +
            frame(1, 0, 0, bytes(7, 7))

        for (chunk in listOf(stream.size, 1, 11, 4999)) {
            val parser = FrameParser()
            val channels = ArrayList<Int>()
            var offset = 0
            while (offset < stream.size) {
                val take = minOf(chunk, stream.size - offset)
                parser.append(stream, offset, take) { header, _, _ -> channels += header.channel }
                offset += take
            }

            assertEquals(listOf(0, 1), channels)
            assertEquals(2, parser.framesSkipped)
            assertTrue(parser.isAtFrameBoundary)
        }
    }

    @Test
    fun `an unknown message type on a known channel is delivered for the caller to ignore`() {
        val stream = Protocol.PREFACE + frame(0, 200, 0, bytes(1, 2))

        val frame = parse(stream, 3).single()

        assertEquals(200, frame.header.type)
        assertNull(ControlCodec.decode(frame.header.type, frame.payload))
    }

    @Test
    fun `header fields are big-endian and unsigned`() {
        val written = header(1, 2, 0x0102, 0x00030405)
        assertArrayEquals(bytes(1, 2, 0x01, 0x02, 0x00, 0x03, 0x04, 0x05), written)
        assertEquals(FrameHeader(1, 2, 0x0102, 0x00030405), FrameHeader.read(written))

        val extreme = FrameHeader.read(bytes(0xFF, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF))
        assertEquals(FrameHeader(255, 254, 0xFFFF, 0xFFFFFFFFL), extreme)
    }

    @Test
    fun `the reused payload buffer does not leak one frame into the next`() {
        val stream = Protocol.PREFACE + frame(0, 1, 0, pattern(100)) + frame(0, 1, 0, bytes(9, 9, 9))

        assertArrayEquals(bytes(9, 9, 9), parse(stream, 7)[1].payload)
    }

    @Test
    fun `only a keyframe that carries its configuration is a random access point`() {
        assertTrue(FrameHeader(1, 0, 3, 0).isRandomAccessPoint)
        assertFalse(FrameHeader(1, 0, 1, 0).isRandomAccessPoint)
        assertFalse(FrameHeader(1, 0, 2, 0).isRandomAccessPoint)
        assertFalse(FrameHeader(1, 0, 0, 0).isRandomAccessPoint)
        assertTrue(FrameHeader(1, 0, 0xFFFF, 0).isRandomAccessPoint)
    }

    private fun parse(stream: ByteArray, chunk: Int): List<Parsed> {
        val parser = FrameParser()
        val frames = ArrayList<Parsed>()
        var offset = 0
        while (offset < stream.size) {
            val take = minOf(chunk, stream.size - offset)
            parser.append(stream, offset, take) { header, payload, length -> frames += Parsed(header, payload.copyOf(length)) }
            offset += take
        }
        return frames
    }

    private fun frame(channel: Int, type: Int, flags: Int, payload: ByteArray): ByteArray =
        header(channel, type, flags, payload.size.toLong()) + payload

    private fun header(channel: Int, type: Int, flags: Int, length: Long): ByteArray =
        ByteArray(Protocol.HEADER_LENGTH).also { FrameHeader(channel, type, flags, length).write(it) }

    private fun bytes(vararg values: Int): ByteArray = ByteArray(values.size) { values[it].toByte() }

    private fun pattern(count: Int): ByteArray = ByteArray(count) { (it * 31 + 7).toByte() }
}
