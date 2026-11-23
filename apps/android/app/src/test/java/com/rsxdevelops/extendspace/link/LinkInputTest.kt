package com.rsxdevelops.extendspace.link

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.connection.VideoSink
import com.rsxdevelops.extendspace.protocol.InputFrames
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.StreamReady
import com.rsxdevelops.extendspace.protocol.TouchContact
import com.rsxdevelops.extendspace.protocol.TouchPhase
import com.rsxdevelops.extendspace.video.HostClockEstimate
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import kotlin.concurrent.thread

class TouchFrameVectorTest {
    @Test
    fun `touch payloads match the shared vectors`() {
        val cases = JSONObject(locate("tests/input-vectors/touch-v1.json").readText()).getJSONArray("cases")
        assertTrue(cases.length() >= 5)
        for (index in 0 until cases.length()) {
            val case = cases.getJSONObject(index)
            val contacts = case.getJSONArray("contacts").let { array ->
                (0 until array.length()).map { array.getJSONObject(it) }.map { TouchContact(it.getInt("id"), it.getInt("phase"), it.getInt("x"), it.getInt("y")) }
            }
            val expected = case.getString("payload")
            val actual = InputFrames.touchPayload(case.getLong("sequence"), contacts).joinToString("") { "%02x".format(it.toInt() and 0xFF) }
            assertEquals(case.getString("name"), expected, actual)
        }
    }

    @Test
    fun `a whole frame is the input channel's header and the payload`() {
        val frame = InputFrames.touchFrame(7, listOf(TouchContact(0, TouchPhase.DOWN, 1, 2))).bytes
        assertEquals(Protocol.CHANNEL_INPUT, frame[0].toInt())
        assertEquals(InputFrames.TOUCH_TYPE, frame[1].toInt())
        assertEquals(16, frame[7].toInt())
        assertArrayEquals(InputFrames.touchPayload(7, listOf(TouchContact(0, TouchPhase.DOWN, 1, 2))), frame.copyOfRange(Protocol.HEADER_LENGTH, frame.size))
    }

    private fun locate(relative: String): File {
        var directory: File? = File("").absoluteFile
        while (directory != null) {
            File(directory, relative).takeIf { it.isFile }?.let { return it }
            directory = directory.parentFile
        }
        error("'$relative' was not found above ${File("").absolutePath}.")
    }
}

class LinkInputTest {
    private val factory = MemoryListenerFactory()
    private val sink = VideoSink { _, _, _ -> }
    private val options = UsbHostLinkOptions(handshakeStepMillis = 1_000, keepaliveMillis = 2_000, statsIntervalMillis = 100, allowedPeerUids = setOf(0, 2000))
    private val link = UsbHostLink(factory, "Fake Pad", { "9.9.9" }, { DisplayInfo(3200, 2136, 144.0) }, { null }, HostClockEstimate(), options)
    private val decoders = listOf(DecoderCapability("c2.fake.avc.decoder", true, true, 4096, 4096))
    private val touch = InputFrames.touchFrame(0, listOf(TouchContact(0, TouchPhase.DOWN, 100, 200)))

    @After
    fun tearDown() = runBlocking {
        link.endLink("test over")
        link.stopListening()
    }

    private fun streaming(inputOffered: Boolean): Pair<TestHost, StreamDescription> = runBlocking {
        val accepting = async(Dispatchers.Default) { link.awaitHost() }
        while (synchronized(factory.listeners) { factory.listeners.isEmpty() }) Thread.sleep(1)
        val connection = MemoryConnection()
        factory.listener.connect(connection)
        withTimeout(PATIENCE_MS) { accepting.await() }
        val host = TestHost(connection)
        val hostSide = thread { host.handshake(TestHost.CONFIG_1080P60.copy(inputTouch = inputOffered)) }
        val stream = (link.exchangeCapabilities(decoders) as AttemptOutcome.Succeeded).value
        hostSide.join(PATIENCE_MS)
        assertEquals(AttemptOutcome.Succeeded(Unit), link.acceptStream(DecoderChoice("c2.fake.avc.decoder", true), sink))
        host.expect<StreamReady>()
        host to stream
    }

    @Test
    fun `on a stream that offered input a touch frame goes to the PC on the input channel`() {
        val (host, stream) = streaming(inputOffered = true)
        assertTrue(stream.inputOffered)

        assertTrue(link.sendInput(touch))

        val (header, payload) = host.nextOn(Protocol.CHANNEL_INPUT)
        assertEquals(InputFrames.TOUCH_TYPE, header.type)
        assertArrayEquals(touch.bytes.copyOfRange(Protocol.HEADER_LENGTH, touch.bytes.size), payload)
    }

    @Test
    fun `on a stream that did not offer input nothing is sent`() {
        val (host, stream) = streaming(inputOffered = false)
        assertFalse(stream.inputOffered)

        assertFalse(link.sendInput(touch))

        assertEquals(0, runCatching { host.nextOn(Protocol.CHANNEL_INPUT, within = 300) }.getOrNull()?.let { 1 } ?: 0)
    }

    @Test
    fun `before a stream and after the link ends nothing is sent`() {
        assertFalse(link.sendInput(touch))
        streaming(inputOffered = true)
        runBlocking { link.endLink("done") }
        assertFalse(link.sendInput(touch))
    }
}
