package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.VideoOutput
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.video.h264.SequenceParameterSet

/** Annex B access units built from real parameter sets (NVIDIA, as in SequenceParameterSetTest) and placeholder slices. */
internal object H264Samples {
    val SPS_1080P = hex("6764002aac2b403c0113f2e022000007d00003a9801e385540")
    val SPS_720P = hex("674d401f95a014016ec04400000fa00003a9803c70aa80")
    val PPS = hex("68ee3c80")
    private val IDR_SLICE = hex("65888421a0")
    private val NON_IDR_SLICE = hex("419a246c41")
    private val START = hex("00000001")

    fun keyframe(sps: ByteArray = SPS_1080P): ByteArray = START + sps + START + PPS + START + IDR_SLICE

    val delta: ByteArray = START + NON_IDR_SLICE

    /** A keyframe flagged CONFIG that carries no parameter sets: malformed. */
    val keyframeWithoutParameterSets: ByteArray = START + IDR_SLICE

    const val KEYFRAME_FLAGS = Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG

    /** A video payload as the link hands it to the sink: the 12-byte video header, then the access unit. */
    fun payload(sequence: Long, accessUnit: ByteArray, timestampMicros: Long = 1_000): ByteArray {
        val payload = ByteArray(Protocol.VIDEO_HEADER_LENGTH + accessUnit.size)
        for (i in 0 until 4) payload[i] = (sequence ushr (24 - 8 * i)).toByte()
        for (i in 0 until 8) payload[4 + i] = (timestampMicros ushr (56 - 8 * i)).toByte()
        accessUnit.copyInto(payload, Protocol.VIDEO_HEADER_LENGTH)
        return payload
    }

    fun hex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

/** An output the tests lend; stands for a surface. */
internal class FakeOutput(val name: String) : VideoOutput {
    override fun toString() = "output $name"
}

internal class FakeDecoder(
    val output: VideoOutput,
    val parameters: SequenceParameterSet,
    override val configuredWith: ByteArray,
    val listener: FrameDecoderListener,
    private val duringRelease: () -> Unit = {},
    private val duringSubmit: () -> Unit = {},
    /** Called when two operations overlap on this decoder: the receiver must never let that happen. */
    private val onOverlap: (String) -> Unit = {},
    /**
     * Null: every picture is taken at once (the default). Otherwise the decoder's real input
     * bookkeeping (D60, D61): pictures wait for input buffers the test hands out with [codecTakes].
     */
    private val backlog: InputBacklog<Pair<Long, Boolean>>? = null,
    private val nowNanos: () -> Long = { 0L },
) : FrameDecoder {
    @Volatile var released = 0
        private set

    private val busy = java.util.concurrent.atomic.AtomicReference<String?>(null)

    private inline fun <T> exclusive(operation: String, block: () -> T): T {
        val other = busy.getAndSet(operation)
        if (other != null) onOverlap("$operation while $other on decoder for $output")
        try {
            return block()
        } finally {
            busy.compareAndSet(operation, null)
        }
    }

    val submitted = mutableListOf<Pair<Long, Boolean>>()

    /** With a backlog: the pictures that reached the codec, in the order they did. */
    val codecInput = mutableListOf<Pair<Long, Boolean>>()

    /** False to refuse every picture, as a decoder that has fallen too far behind does. */
    @Volatile var accepting = true

    override val queueDepth: Int get() = 0
    override val lowLatency: String = "requested; the fake reports the feature"

    override fun submit(data: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Boolean = exclusive("submit") {
        duringSubmit()
        if (released > 0 || !accepting) return@exclusive false
        val queue = backlog
        if (queue != null) {
            val taken = synchronized(this) {
                when (val offer = queue.offer(nowNanos(), length, keyFrame) { timestampMicros to keyFrame }) {
                    is InputBacklog.Offer.Input -> codecInput.add(timestampMicros to keyFrame).let { true }
                    InputBacklog.Offer.Waiting -> true
                    is InputBacklog.Offer.Refused -> false
                }
            }
            if (!taken) return@exclusive false
        }
        synchronized(submitted) { submitted.add(timestampMicros to keyFrame) }
        true
    }

    override fun takeSuperseded(): Int = backlog?.let { synchronized(this) { it.takeSuperseded() } } ?: 0

    /** With a backlog: the codec offers [count] input buffers, which take waiting pictures in order or are kept free. */
    fun codecTakes(count: Int) = synchronized(this) {
        repeat(count) { index -> backlog!!.inputAvailable(nowNanos(), index)?.let { codecInput.add(it) } }
    }

    /** With a backlog: the codec has produced a picture. */
    fun producedOutput() = synchronized(this) { backlog!!.outputProduced(nowNanos()) }

    /** With a backlog: pictures waiting for an input buffer. */
    val waiting: Int get() = synchronized(this) { backlog!!.depth }

    override fun release() = exclusive("release") {
        duringRelease()
        released += 1
    }
}

internal class FakePlatform : DecoderPlatform {
    val created = mutableListOf<FakeDecoder>()

    /** Thrown by the next decoder created, instead of creating it. */
    @Volatile var createFailure: Exception? = null

    /** Runs inside [create], before the decoder exists; tests block in it to hold a creation open. */
    @Volatile var duringCreate: () -> Unit = {}

    /** Runs inside each decoder's release, before it counts as released; tests block in it to hold a release open. */
    @Volatile var duringRelease: () -> Unit = {}

    /** Runs inside each submission; tests slow it down to widen the window for overlapping operations. */
    @Volatile var duringSubmit: () -> Unit = {}

    /** Operations that overlapped on one decoder. Must stay empty. */
    val overlaps: MutableList<String> = java.util.Collections.synchronizedList(mutableListOf())

    /** Creations running right now, and the most ever at once. The receiver creates one at a time. */
    private val creating = java.util.concurrent.atomic.AtomicInteger()
    @Volatile var mostCreatingAtOnce = 0
        private set

    /** True: decoders keep the real input bookkeeping, and the test hands out input buffers ([FakeDecoder.codecTakes]). */
    @Volatile var withBacklog = false

    /** The time the decoders' bookkeeping sees, in nanoseconds. */
    @Volatile var nowNanos = 0L

    /** Null: no decoder claims the size. */
    @Volatile var choice: DecoderChoice? = DecoderChoice("c2.fake.avc.decoder", true)

    val live: List<FakeDecoder> get() = synchronized(created) { created.filter { it.released == 0 } }

    override fun decoders() = listOf(DecoderCapability("c2.fake.avc.decoder", hardware = true, lowLatency = true, maxWidth = 4096, maxHeight = 4096))

    override fun choose(width: Int, height: Int): DecoderChoice? = choice

    override fun create(
        output: VideoOutput,
        parameters: SequenceParameterSet,
        spsNal: ByteArray,
        ppsNal: ByteArray,
        decoder: DecoderChoice,
        listener: FrameDecoderListener,
    ): FrameDecoder {
        val now = creating.incrementAndGet()
        try {
            synchronized(this) { mostCreatingAtOnce = maxOf(mostCreatingAtOnce, now) }
            createFailure?.let { throw it }
            duringCreate()
            val backlog = if (withBacklog) InputBacklog<Pair<Long, Boolean>>().apply { started(nowNanos) } else null
            return FakeDecoder(output, parameters, spsNal + ppsNal, listener, { duringRelease() }, { duringSubmit() }, { overlaps.add(it) }, backlog, { nowNanos })
                .also { synchronized(created) { created.add(it) } }
        } finally {
            creating.decrementAndGet()
        }
    }
}

/** A decoder owner that runs each task at once on the calling thread: creation is then as synchronous as it used to be. */
internal object InlineOwner : java.util.concurrent.Executor {
    override fun execute(command: Runnable) = command.run()
}

/** A decoder owner the test steps by hand: nothing runs until [runAll]. */
internal class ManualOwner : java.util.concurrent.Executor {
    private val tasks = java.util.concurrent.LinkedBlockingQueue<Runnable>()

    override fun execute(command: Runnable) {
        tasks.add(command)
    }

    val queued: Int get() = tasks.size

    /** Runs everything queued, and anything queued meanwhile, on the calling thread. */
    fun runAll() {
        while (true) (tasks.poll() ?: return).run()
    }
}
