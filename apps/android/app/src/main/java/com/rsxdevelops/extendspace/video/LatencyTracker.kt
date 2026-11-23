package com.rsxdevelops.extendspace.video

// Copied from the debug experiment (debug/.../livestream/LatencyTracker.kt) for the production
// receiver and link, package changed only (decision D52).

import com.rsxdevelops.extendspace.protocol.LatencySummary
import kotlin.math.ceil

/**
 * This device's monotonic clock and the host's have unrelated origins. The host works out the
 * difference from ping and pong and sends it; with it, a moment on this device can be placed on
 * the host's clock and compared with the capture timestamp a frame carries.
 *
 * The estimate is good to half the round trip it was taken from, so every latency derived from it
 * carries that uncertainty.
 */
internal class HostClockEstimate {
    @Volatile
    private var offsetMicros: Long? = null

    /** The round trip the current estimate came from, or null before the host has sent one. */
    @Volatile
    var roundTripMicros: Long? = null
        private set

    val isKnown: Boolean get() = offsetMicros != null

    /** @param clientMinusHostMicros this device's clock minus the host's, as the host estimates it. */
    fun update(clientMinusHostMicros: Long, roundTripMicros: Long?) {
        offsetMicros = clientMinusHostMicros
        this.roundTripMicros = roundTripMicros
    }

    fun clear() {
        offsetMicros = null
        roundTripMicros = null
    }

    /**
     * How long after [hostCaptureMicros] (on the host's clock) the moment [clientMicros] (on this
     * device's clock) was, or null while the clocks cannot be related.
     */
    fun latencyMicros(clientMicros: Long, hostCaptureMicros: Long): Long? =
        offsetMicros?.let { clientMicros - it - hostCaptureMicros }
}

/** Collects latency samples between two reports. Thread-safe: frames are timed on several threads. */
internal class LatencyTracker(private val capacity: Int = DEFAULT_CAPACITY) {
    private val samples = LongArray(capacity)
    private var count = 0

    @Synchronized
    fun add(latencyMicros: Long) {
        // Beyond the capacity, which is several seconds of video, the newest samples are dropped
        // rather than the report delayed; the count in the summary says how many it covers.
        if (count < capacity) samples[count++] = latencyMicros
    }

    /** Summarises what was collected since the last call and starts again. Null if there was nothing. */
    @Synchronized
    fun drain(): LatencySummary? {
        if (count == 0) return null
        val sorted = samples.copyOf(count).apply { sort() }
        count = 0
        return LatencySummary(
            count = sorted.size,
            minMs = sorted.first() / MICROS_PER_MILLI,
            meanMs = sorted.average() / MICROS_PER_MILLI,
            p95Ms = sorted[percentileIndex(sorted.size, 95.0)] / MICROS_PER_MILLI,
            maxMs = sorted.last() / MICROS_PER_MILLI,
        )
    }

    companion object {
        const val DEFAULT_CAPACITY = 2_048
        private const val MICROS_PER_MILLI = 1_000.0

        /** Nearest-rank percentile: the index of the value at or above which [percentile] percent of values lie. */
        internal fun percentileIndex(size: Int, percentile: Double): Int =
            (ceil(percentile / 100.0 * size).toInt()).coerceIn(1, size) - 1
    }
}
