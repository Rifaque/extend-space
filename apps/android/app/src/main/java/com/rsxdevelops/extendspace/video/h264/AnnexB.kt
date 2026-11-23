package com.rsxdevelops.extendspace.video.h264

// Copied from the debug experiment (debug/.../decodertest/h264/AnnexB.kt)
// for the production link and receiver, package changed only (migration stage 5, decision D52).
// The debug copy stays until the experiment is retired (stage 8).

import java.io.IOException

/** The file is not a usable H.264 Annex B elementary stream. The message says why. */
class MalformedStreamException(message: String) : IOException(message)

/** H.264 `nal_unit_type` values this code distinguishes (ITU-T H.264, table 7-1). */
internal object NalType {
    const val NON_IDR_SLICE = 1
    const val IDR_SLICE = 5
    const val SEI = 6
    const val SPS = 7
    const val PPS = 8
    const val ACCESS_UNIT_DELIMITER = 9

    /** Types 1 to 5 carry coded slice data. */
    fun isVcl(type: Int): Boolean = type in 1..5

    fun of(header: Byte): Int = header.toInt() and 0x1F
}

/**
 * One NAL unit inside a buffer.
 *
 * @property start where its start code begins, including the leading zero of a four-byte code.
 * @property payload where the NAL unit itself begins, at its header byte.
 * @property end one past its last byte, with trailing zero padding excluded.
 */
internal class NalUnit(val type: Int, val start: Int, val payload: Int, val end: Int) {
    val length: Int get() = end - payload
}

internal object AnnexB {
    /**
     * Finds the three-byte start code `00 00 01` at or after [from], looking no further than [to].
     * Returns the index of its first byte, or -1.
     */
    fun indexOfStartCode(data: ByteArray, from: Int, to: Int): Int {
        var i = from
        val last = to - 3
        while (i <= last) {
            // The third byte decides how far the search can jump, as in a Boyer-Moore search.
            val third = data[i + 2].toInt()
            if (third > 1 || third < 0) {
                i += 3
            } else if (third == 1) {
                if (data[i].toInt() == 0 && data[i + 1].toInt() == 0) return i
                i += 3
            } else {
                i += 1
            }
        }
        return -1
    }

    /** Splits a complete buffer into NAL units. Bytes before the first start code are ignored. */
    fun findNalUnits(data: ByteArray, from: Int = 0, to: Int = data.size): List<NalUnit> {
        val units = ArrayList<NalUnit>()
        var startCode = indexOfStartCode(data, from, to)
        while (startCode >= 0) {
            val payload = startCode + 3
            val next = indexOfStartCode(data, payload, to)
            var end = if (next >= 0) next else to
            // A NAL unit never ends in a zero byte: zeros here are padding or the leading zero of
            // the next, four-byte, start code.
            while (end > payload && data[end - 1].toInt() == 0) end--
            if (end > payload) {
                val fourByte = startCode > from && data[startCode - 1].toInt() == 0
                units += NalUnit(NalType.of(data[payload]), if (fourByte) startCode - 1 else startCode, payload, end)
            }
            startCode = next
        }
        return units
    }
}
