package com.rsxdevelops.extendspace.link

import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ControlMessage
import com.rsxdevelops.extendspace.protocol.FrameHeader
import com.rsxdevelops.extendspace.protocol.FrameParser
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.StreamConfig
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.ArrayDeque
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** The failsafe on every wait in these tests. A passing test never comes near it. */
internal const val PATIENCE_MS = 10_000L

/**
 * One direction of an in-memory connection. Reads block until bytes arrive, the pipe is closed, or
 * the read timeout passes, which throws as `LocalSocket` does ("Try again"). A capacity makes writes
 * block, for a peer that stops reading.
 */
internal class MemoryPipe(private val capacity: Int = Int.MAX_VALUE) {
    private val lock = Object()
    private val bytes = ArrayDeque<Byte>()
    private var closed = false
    private var writerDone = false

    @Volatile var readTimeoutMillis = 0

    /** How much sooner than [readTimeoutMillis] a read gives up, as a kernel timer counting whole ticks can. */
    @Volatile var timeoutEarlyMillis = 0

    val input: InputStream = object : InputStream() {
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int = synchronized(lock) {
            val deadline = if (readTimeoutMillis > 0) System.nanoTime() + (readTimeoutMillis - timeoutEarlyMillis) * 1_000_000L else Long.MAX_VALUE
            while (bytes.isEmpty()) {
                if (closed) throw IOException("Socket closed")
                if (writerDone) return -1
                val waitMillis = (deadline - System.nanoTime()) / 1_000_000
                if (waitMillis <= 0) throw IOException("Try again")
                lock.wait(minOf(waitMillis, 50))
            }
            var n = 0
            while (n < len && bytes.isNotEmpty()) b[off + n++] = bytes.removeFirst()
            lock.notifyAll()
            n
        }
    }

    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

        override fun write(b: ByteArray, off: Int, len: Int) = synchronized(lock) {
            for (i in off until off + len) {
                while (bytes.size >= capacity) {
                    if (closed || writerDone) throw IOException("Broken pipe")
                    lock.wait(50)
                }
                if (closed || writerDone) throw IOException("Broken pipe")
                bytes.addLast(b[i])
            }
            lock.notifyAll()
        }
    }

    /** The writer's end closed in an orderly way: what was written is still read, then end of stream. */
    fun finishWriting() = synchronized(lock) {
        writerDone = true
        lock.notifyAll()
    }

    /** Closed outright: reads and writes, blocked or not, fail. */
    fun close() = synchronized(lock) {
        closed = true
        lock.notifyAll()
    }
}

/** Both directions of one connection, and the two ends: the app's [HostConnection] and the [TestHost]. */
internal class MemoryConnection(toAppCapacity: Int = Int.MAX_VALUE, toHostCapacity: Int = Int.MAX_VALUE, override val peerUid: Int? = 2000) : HostConnection {
    val toApp = MemoryPipe(toAppCapacity)
    val toHost = MemoryPipe(toHostCapacity)

    @Volatile var closes = 0
        private set

    override val input: InputStream get() = toApp.input
    override val output: OutputStream get() = toHost.output

    override fun setReadTimeout(millis: Int) {
        toApp.readTimeoutMillis = millis
    }

    override fun close() {
        closes++
        toApp.close()
        toHost.close()
    }
}

/** A listener whose connections are handed in by the test. */
internal class MemoryListener : HostListener {
    private val pending = LinkedBlockingQueue<HostConnection>()

    @Volatile var closed = false
        private set

    override fun accept(): HostConnection {
        while (true) {
            if (closed) throw IOException("The listener was closed.")
            pending.poll(20, TimeUnit.MILLISECONDS)?.let { return it }
        }
    }

    override fun close() {
        closed = true
    }

    fun connect(connection: HostConnection) = pending.add(connection)
}

internal class MemoryListenerFactory : HostListenerFactory {
    val listeners = mutableListOf<MemoryListener>()

    @Volatile var failure: IOException? = null

    val listener: MemoryListener get() = synchronized(listeners) { listeners.last() }

    override fun listen(): HostListener {
        failure?.let { throw it }
        return MemoryListener().also { synchronized(listeners) { listeners.add(it) } }
    }
}

/** The Windows host's side of the protocol, reduced to what these tests need. */
internal class TestHost(val connection: MemoryConnection) {
    private val parser = FrameParser()
    private val frames = ArrayDeque<Pair<FrameHeader, ByteArray>>()
    private val buffer = ByteArray(64 * 1024)
    private var sequence = 0L

    fun send(message: ControlMessage) = connection.toApp.output.write(ControlCodec.encode(message))

    fun sendPreface() = connection.toApp.output.write(Protocol.PREFACE)

    /** The host's half of the handshake up to `stream_config`. Returns the client's hello. */
    fun handshake(config: StreamConfig = CONFIG_1080P60): Hello {
        sendPreface()
        send(Hello(0, 0, "host", "TEST-PC", "1.2.3"))
        val hello = expect<Hello>()
        expect<com.rsxdevelops.extendspace.protocol.ClientCapabilities>()
        send(config)
        return hello
    }

    /** One video frame, numbered in order unless [sequenceNumber] says otherwise. */
    fun sendVideo(accessUnit: ByteArray, flags: Int, timestampMicros: Long = 1_000, sequenceNumber: Long = sequence++) {
        val payload = ByteArray(Protocol.VIDEO_HEADER_LENGTH + accessUnit.size)
        for (i in 0 until 4) payload[i] = (sequenceNumber ushr (24 - 8 * i)).toByte()
        for (i in 0 until 8) payload[4 + i] = (timestampMicros ushr (56 - 8 * i)).toByte()
        accessUnit.copyInto(payload, Protocol.VIDEO_HEADER_LENGTH)
        sendRaw(Protocol.CHANNEL_VIDEO, 0, flags, payload)
    }

    fun sendRaw(channel: Int, type: Int, flags: Int, payload: ByteArray) {
        val frame = ByteArray(Protocol.HEADER_LENGTH + payload.size)
        FrameHeader(channel, type, flags, payload.size.toLong()).write(frame)
        payload.copyInto(frame, Protocol.HEADER_LENGTH)
        connection.toApp.output.write(frame)
    }

    /** The next control message from the app, skipping those [skip] accepts. Throws [EOFException] if the app closed. */
    fun next(within: Long = PATIENCE_MS, skip: (ControlMessage) -> Boolean = { false }): ControlMessage {
        val deadline = System.currentTimeMillis() + within
        while (true) {
            while (frames.isNotEmpty()) {
                val (header, payload) = frames.removeFirst()
                if (header.channel != Protocol.CHANNEL_CONTROL) {
                    others.add(header to payload)
                    continue
                }
                val message = ControlCodec.decode(header.type, payload) ?: continue
                if (!skip(message)) return message
            }
            if (System.currentTimeMillis() >= deadline) throw NothingArrived()
            connection.toHost.readTimeoutMillis = 50
            val read = try {
                connection.toHost.input.read(buffer)
            } catch (failure: IOException) {
                if (failure.message == "Try again") continue
                throw EOFException("The app closed the connection: ${failure.message}")
            }
            if (read < 0) throw EOFException("The app closed the connection.")
            parser.append(buffer, 0, read) { header, payload, length -> frames.add(header to payload.copyOf(length)) }
        }
    }

    /** Frames on channels other than control, in arrival order (input, since D81). */
    val others = ArrayDeque<Pair<FrameHeader, ByteArray>>()

    /** The next frame on [channel], reading control messages past it. */
    fun nextOn(channel: Int, within: Long = PATIENCE_MS): Pair<FrameHeader, ByteArray> {
        val deadline = System.currentTimeMillis() + within
        while (true) {
            others.firstOrNull { it.first.channel == channel }?.let { found ->
                others.remove(found)
                return found
            }
            try {
                next(within = 50) { true }
            } catch (quiet: NothingArrived) {
                if (System.currentTimeMillis() >= deadline) throw quiet
            }
        }
    }

    /** Statistics and pongs come on their own schedule; most tests skip them. */
    inline fun <reified T : ControlMessage> expect(noinline skip: (ControlMessage) -> Boolean = ::isRoutine): T {
        val message = next(skip = skip)
        check(message is T) { "Expected ${T::class.simpleName}, received $message" }
        return message
    }

    /** True if the app closed the connection within [millis], whatever it sent before. */
    fun closedWithin(millis: Long): Boolean = try {
        next(within = millis) { true }
        false
    } catch (closed: EOFException) {
        true
    } catch (quiet: NothingArrived) {
        false
    }

    class NothingArrived : IllegalStateException("Nothing arrived from the app in time.")

    companion object {
        fun isRoutine(message: ControlMessage): Boolean =
            message is com.rsxdevelops.extendspace.protocol.ClientStats || message is com.rsxdevelops.extendspace.protocol.Pong

        val CONFIG_1080P60 = StreamConfig("h264", "high", 1920, 1080, 60, 12_000_000, Protocol.MAX_VIDEO_PAYLOAD, "Test Encoder", "hardware")
    }
}
