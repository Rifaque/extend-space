package com.rsxdevelops.extendspace.protocol

// Copied from the debug experiment (testDebug/.../livestream/protocol/ControlCodecTest.kt)
// for the production link and receiver, package changed only (migration stage 5, decision D52).
// The debug copy stays until the experiment is retired (stage 8).

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ControlCodecTest {
    // The same bytes are decoded by the Windows tests (ControlCodecTests.cs). If the two
    // implementations ever disagree about the wire format, one of these fails.
    private val helloVector =
        "00010000000000627b2270726f746f636f6c5f6d696e223a302c2270726f746f636f6c5f6d6178223a302c22726f6c65223a22636c69656e74222c226465766963655f6e616d65223a225465737420506164222c226170705f76657273696f6e223a22302e312e30227d"
    private val pingVector =
        "00060000000000577b226964223a372c22686f73745f74696d655f7573223a3132333435363738393031322c22636c6f636b5f6f66667365745f7573223a2d353030303030303132332c22726f756e645f747269705f7573223a313834307d"
    private val videoVector = "0100000300000012000000070000001cbe991a14000000016588"

    @Test
    fun `the shared hello vector decodes`() {
        val (header, payload) = split(hex(helloVector))

        assertEquals(FrameHeader(Protocol.CHANNEL_CONTROL, ControlType.HELLO, 0, 98), header)
        assertEquals(Hello(0, 0, "client", "Test Pad", "0.1.0"), ControlCodec.decode(header.type, payload))
    }

    @Test
    fun `the shared ping vector decodes`() {
        val (header, payload) = split(hex(pingVector))

        assertEquals(ControlType.PING, header.type)
        assertEquals(Ping(7, 123_456_789_012, -5_000_000_123, 1840), ControlCodec.decode(header.type, payload))
    }

    @Test
    fun `the shared video vector has the fields the host put in it`() {
        val (header, payload) = split(hex(videoVector))

        assertEquals(FrameHeader(Protocol.CHANNEL_VIDEO, 0, Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG, 18), header)
        // Sequence 7, then timestamp 123,456,789,012 microseconds, then the access unit.
        assertArrayEquals(byteArrayOf(0, 0, 0, 7), payload.copyOfRange(0, 4))
        assertEquals(123_456_789_012, payload.copyOfRange(4, 12).fold(0L) { value, byte -> (value shl 8) or (byte.toLong() and 0xFF) })
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x65, 0x88.toByte()), payload.copyOfRange(12, payload.size))
    }

    @Test
    fun `every message survives encoding and decoding`() {
        val messages = listOf(
            Hello(0, 0, "client", "Xiaomi 2410CRP4CI", "0.1.0"),
            ClientCapabilities(
                3200,
                2136,
                120.0,
                listOf(
                    DecoderCapability("c2.vendor.avc.decoder", hardware = true, lowLatency = false, maxWidth = 4096, maxHeight = 4096),
                    DecoderCapability("c2.android.avc.decoder", hardware = false, lowLatency = false, maxWidth = 4096, maxHeight = 4096),
                ),
            ),
            StreamConfig("h264", "high", 1920, 1080, 60, 12_000_000, Protocol.MAX_VIDEO_PAYLOAD, "Some Encoder MFT", "hardware"),
            StreamConfig("h264", "main", 1280, 720, 30, 6_000_000, 1_000_000, null, null),
            StreamReady("c2.vendor.avc.decoder", true),
            StreamReady(null, null),
            KeyframeRequest("surface_recreated"),
            Ping(1, 5_000, null, null),
            Ping(2, 6_000, -42, 1_500),
            Pong(2, 6_000, 987_654_321_000),
            ClientStats(
                framesReceived = 600,
                bytesReceived = 4_000_000,
                framesMissing = 2,
                framesDiscarded = 3,
                framesSubmitted = 597,
                framesDecoded = 596,
                framesRendered = 596,
                framesDisplayed = 590,
                decoderErrors = 1,
                queueDepth = 2,
                receiveLatency = LatencySummary(60, 4.5, 6.25, 9.75, 12.0),
                renderLatency = LatencySummary(60, 14.5, 18.25, 25.0, 31.5),
                displayLatency = null,
            ),
            ErrorMessage(ErrorCode.STREAM_CONFIG_REJECTED, "no decoder for 8192 x 8192", true),
            Bye("the live screen was closed on the device"),
        )

        for (message in messages) {
            val (header, payload) = split(ControlCodec.encode(message))

            assertEquals(Protocol.CHANNEL_CONTROL, header.channel)
            assertEquals(payload.size.toLong(), header.length)
            assertEquals(message, ControlCodec.decode(header.type, payload))
        }
    }

    @Test
    fun `unknown fields are ignored`() {
        val json = """{"reason":"decode_error","added_in_a_later_version":{"x":[1,2,3]},"n":1}"""

        assertEquals(KeyframeRequest("decode_error"), ControlCodec.decode(ControlType.REQUEST_KEYFRAME, json.toByteArray()))
    }

    @Test
    fun `an unknown control type is not an error`() {
        assertNull(ControlCodec.decode(0, "{}".toByteArray()))
        // 11 to 14 became the pairing messages (D77); 15 is the next unassigned type.
        assertNull(ControlCodec.decode(15, """{"anything":true}""".toByteArray()))
        assertNull(ControlCodec.decode(255, byteArrayOf(0xFF.toByte(), 0xFE.toByte())))
    }

    @Test
    fun `a ping that is not what a ping must be is malformed`() {
        val bad = listOf(
            "",
            "not json",
            "[1,2,3]",
            "42",
            """{"id":1""",
            """{"id":"one","host_time_us":2}""",
            """{"id":"1","host_time_us":2}""",
            """{"id":1}""",
            """{"id":1.5,"host_time_us":2}""",
            """{"id":99999999999999999999,"host_time_us":2}""",
            """{"id":true,"host_time_us":2}""",
            """{"id":null,"host_time_us":2}""",
        )
        for (json in bad) {
            val exception = assertThrows(json, ProtocolException::class.java) { ControlCodec.decode(ControlType.PING, json.toByteArray()) }
            assertEquals(ErrorCode.MALFORMED_MESSAGE, exception.code)
        }
    }

    @Test
    fun `fields of the wrong type are malformed rather than coerced`() {
        assertThrows(ProtocolException::class.java) {
            ControlCodec.decode(ControlType.STREAM_CONFIG, """{"codec":264,"profile":"high","width":1,"height":1,"frame_rate":1,"bitrate":1,"max_video_payload":1}""".toByteArray())
        }
        assertThrows(ProtocolException::class.java) {
            ControlCodec.decode(ControlType.STREAM_CONFIG, """{"codec":"h264","profile":"high","width":"1280","height":720,"frame_rate":30,"bitrate":1,"max_video_payload":1}""".toByteArray())
        }
        assertThrows(ProtocolException::class.java) {
            ControlCodec.decode(ControlType.STREAM_CONFIG, """{"codec":"h264","profile":"high","width":99999999999,"height":720,"frame_rate":30,"bitrate":1,"max_video_payload":1}""".toByteArray())
        }
        assertThrows(ProtocolException::class.java) { ControlCodec.decode(ControlType.ERROR, """{"message":"no code"}""".toByteArray()) }
    }

    @Test
    fun `json nested deeper than eight levels is rejected`() {
        fun nested(depth: Int) = """{"reason":"x","n":""" + """{"n":""".repeat(depth - 1) + "1" + "}".repeat(depth)

        assertNotNull(ControlCodec.decode(ControlType.REQUEST_KEYFRAME, nested(8).toByteArray()))
        assertThrows(ProtocolException::class.java) { ControlCodec.decode(ControlType.REQUEST_KEYFRAME, nested(9).toByteArray()) }
    }

    @Test
    fun `nesting depth ignores brackets inside strings`() {
        assertEquals(1, ControlCodec.nestingDepth("""{"a":"{[{[{["}"""))
        assertEquals(1, ControlCodec.nestingDepth("""{"a":"quote \" then {{{"}"""))
        assertEquals(3, ControlCodec.nestingDepth("""{"a":[{"b":1}],"c":{}}"""))
        assertEquals(0, ControlCodec.nestingDepth("42"))
    }

    @Test
    fun `a decoder for another codec is left out rather than misread`() {
        val json =
            """{"display":{"width":1,"height":1},"decoders":[{"codec":"hevc","name":"h","hardware":true,"max_width":8192,"max_height":8192},{"codec":"h264","name":"a","hardware":false,"max_width":1920,"max_height":1088}]}"""

        val capabilities = ControlCodec.decode(ControlType.CAPABILITIES, json.toByteArray()) as ClientCapabilities

        assertEquals(listOf("a"), capabilities.decoders.map { it.name })
        assertEquals(0.0, capabilities.refreshHz, 0.0)
    }

    @Test
    fun `only the stated part of the payload buffer is decoded`() {
        val json = """{"reason":"startup"}""".toByteArray()
        val reusedBuffer = json + """ leftover from an earlier, longer frame"}""".toByteArray()

        assertEquals(KeyframeRequest("startup"), ControlCodec.decode(ControlType.REQUEST_KEYFRAME, reusedBuffer, json.size))
    }

    private fun split(frame: ByteArray): Pair<FrameHeader, ByteArray> =
        FrameHeader.read(frame) to frame.copyOfRange(Protocol.HEADER_LENGTH, frame.size)

    private fun hex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
