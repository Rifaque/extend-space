package com.rsxdevelops.extendspace.decodertest.h264

import java.io.Closeable
import java.io.InputStream

/**
 * One coded picture with everything that belongs to it: the bytes exactly as they were in the
 * stream, start codes included, and where each NAL unit lies within them.
 */
internal class AccessUnit(val data: ByteArray, val nalUnits: List<NalUnit>) {
    val isIdr: Boolean get() = nalUnits.any { it.type == NalType.IDR_SLICE }

    /** The first NAL unit of [type], without its start code, or null. */
    fun firstOfType(type: Int): ByteArray? =
        nalUnits.firstOrNull { it.type == type }?.let { data.copyOfRange(it.payload, it.end) }
}

/**
 * Reads an H.264 Annex B elementary stream one access unit at a time, which is what a decoder has
 * to be fed: a whole picture per input buffer.
 *
 * A picture can be several NAL units (parameter sets, SEI, and one slice NAL unit *per slice*), so
 * NAL boundaries are not picture boundaries. A new access unit starts, once the current one already
 * holds slice data, at the first NAL unit that is an access unit delimiter, SPS, PPS, SEI or one of
 * types 14 to 18, or a slice whose `first_mb_in_slice` is zero (H.264 section 7.4.1.2.3). That rule
 * is exact for streams without arbitrary slice order or redundant pictures, which no encoder used
 * by ExtendSpace produces.
 *
 * Memory use is bounded by the largest access unit, not by the file.
 */
internal class AccessUnitReader(
    private val input: InputStream,
    private val maxAccessUnitBytes: Int = DEFAULT_MAX_ACCESS_UNIT_BYTES,
) : Closeable {
    private var buffer = ByteArray(INITIAL_BUFFER_BYTES)
    private var filled = 0
    private var scanFrom = 0
    private var endOfInput = false
    private var finished = false

    // The NAL unit whose end has not been seen yet.
    private var pendingStart = -1
    private var pendingPayload = -1

    // The access unit being collected.
    private var unitsStart = -1
    private val units = ArrayList<NalUnit>()
    private var hasSliceData = false

    private val ready = ArrayDeque<AccessUnit>()

    /** How many access units have been returned so far. */
    var accessUnitsRead: Long = 0
        private set

    /** The next access unit, or null at the end of the stream. */
    fun next(): AccessUnit? {
        while (ready.isEmpty() && !finished) {
            val startCode = AnnexB.indexOfStartCode(buffer, scanFrom, filled)
            if (startCode >= 0) {
                onStartCode(startCode)
            } else if (endOfInput) {
                finish()
            } else {
                // A start code may straddle the boundary between what is here and what comes next.
                scanFrom = maxOf(scanFrom, filled - 2)
                readMore()
            }
        }

        return ready.removeFirstOrNull()?.also { accessUnitsRead++ }
    }

    override fun close() = input.close()

    private fun onStartCode(startCode: Int) {
        val start: Int
        if (pendingPayload >= 0) {
            start = trimmedEnd(pendingPayload, startCode)
            completeNalUnit(pendingStart, pendingPayload, start)
        } else {
            for (i in 0 until startCode) {
                if (buffer[i].toInt() != 0) {
                    throw MalformedStreamException(
                        "The file does not begin with an Annex B start code (00 00 01). " +
                            "It is not a raw H.264 elementary stream; an MP4 or MKV file will not work.",
                    )
                }
            }
            start = 0
        }

        pendingStart = start
        pendingPayload = startCode + 3
        scanFrom = pendingPayload
    }

    private fun finish() {
        finished = true
        if (pendingPayload < 0) {
            // A start code, once seen, always leaves a pending NAL unit: so there never was one.
            throw MalformedStreamException(
                if (filled == 0) {
                    "The file is empty."
                } else {
                    "The file contains no Annex B start code (00 00 01). It is not a raw H.264 elementary stream."
                },
            )
        }

        val end = trimmedEnd(pendingPayload, filled)
        completeNalUnit(pendingStart, pendingPayload, end)
        emit(upTo = end)
    }

    /** Excludes trailing zero bytes, which are padding or belong to the next start code. */
    private fun trimmedEnd(payload: Int, limit: Int): Int {
        var end = limit
        while (end > payload && buffer[end - 1].toInt() == 0) end--
        return end
    }

    private fun completeNalUnit(start: Int, payload: Int, end: Int) {
        if (end <= payload) return // A start code with nothing behind it.

        val type = NalType.of(buffer[payload])
        if (hasSliceData && startsAccessUnit(type, payload, end)) {
            emit(upTo = start)
        }

        if (unitsStart < 0) unitsStart = start
        units += NalUnit(type, start, payload, end)
        if (NalType.isVcl(type)) hasSliceData = true
    }

    private fun startsAccessUnit(type: Int, payload: Int, end: Int): Boolean = when (type) {
        // first_mb_in_slice is the slice header's first field, coded ue(v): zero is the single bit 1.
        in 1..5 -> end - payload >= 2 && (buffer[payload + 1].toInt() and 0x80) != 0
        NalType.SEI, NalType.SPS, NalType.PPS, NalType.ACCESS_UNIT_DELIMITER, in 14..18 -> true
        else -> false
    }

    /** Hands over the collected NAL units as one access unit, if they include a picture. */
    private fun emit(upTo: Int) {
        if (hasSliceData) {
            val base = unitsStart
            ready += AccessUnit(
                buffer.copyOfRange(base, upTo),
                units.map { NalUnit(it.type, it.start - base, it.payload - base, it.end - base) },
            )
        }
        // Without slice data these were stray parameter sets or SEI at the end of the stream.
        units.clear()
        unitsStart = -1
        hasSliceData = false
    }

    private fun readMore() {
        discardConsumed()

        if (filled > maxAccessUnitBytes) {
            throw MalformedStreamException(
                "No picture boundary within ${maxAccessUnitBytes / (1024 * 1024)} MiB. This is not an H.264 Annex B stream, or it is damaged.",
            )
        }

        if (filled == buffer.size) {
            buffer = buffer.copyOf(buffer.size * 2)
        }

        val read = input.read(buffer, filled, buffer.size - filled)
        if (read < 0) endOfInput = true else filled += read
    }

    /** Moves what is still needed to the front of the buffer, so it only ever holds one access unit. */
    private fun discardConsumed() {
        val keepFrom = when {
            unitsStart >= 0 -> unitsStart
            pendingStart >= 0 -> pendingStart
            else -> 0
        }
        if (keepFrom == 0) return

        System.arraycopy(buffer, keepFrom, buffer, 0, filled - keepFrom)
        filled -= keepFrom
        scanFrom -= keepFrom
        if (pendingPayload >= 0) {
            pendingStart -= keepFrom
            pendingPayload -= keepFrom
        }
        if (unitsStart >= 0) {
            unitsStart = 0
            for (i in units.indices) {
                val unit = units[i]
                units[i] = NalUnit(unit.type, unit.start - keepFrom, unit.payload - keepFrom, unit.end - keepFrom)
            }
        }
    }

    companion object {
        /** Far above any real picture: an uncompressed 4096 x 2304 frame is 14 MiB. */
        const val DEFAULT_MAX_ACCESS_UNIT_BYTES = 16 * 1024 * 1024
        private const val INITIAL_BUFFER_BYTES = 256 * 1024
    }
}
