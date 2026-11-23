package com.rsxdevelops.extendspace.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** D90: `stream_config.mode` says the stream is a virtual monitor extending the PC's desktop. */
class ExtendModeProtocolTest {
    private fun decodeConfig(json: String) = ControlCodec.decode(ControlType.STREAM_CONFIG, json.toByteArray()) as StreamConfig

    private val base = """"codec":"h264","profile":"high","width":1620,"height":1080,"frame_rate":60,"bitrate":12000000,"max_video_payload":1048576"""

    @Test
    fun `an extended display is announced and an old host reads as mirror`() {
        assertEquals("extend", decodeConfig("{$base,\"mode\":\"extend\"}").mode)
        assertNull(decodeConfig("{$base}").mode)
    }

    @Test
    fun `the mode round trips`() {
        val config = decodeConfig("{$base}").copy(mode = "extend")
        val frame = ControlCodec.encode(config)
        assertEquals("extend", (ControlCodec.decode(frame[1].toInt(), frame.copyOfRange(Protocol.HEADER_LENGTH, frame.size)) as StreamConfig).mode)
    }
}
