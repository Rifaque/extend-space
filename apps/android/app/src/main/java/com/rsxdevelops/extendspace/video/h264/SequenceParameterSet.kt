package com.rsxdevelops.extendspace.video.h264

// Copied from the debug experiment (debug/.../decodertest/h264/SequenceParameterSet.kt)
// for the production link and receiver, package changed only (migration stage 5, decision D52).
// The debug copy stays until the experiment is retired (stage 8).

/**
 * What a decoder needs to know before the first picture, read from an H.264 sequence parameter
 * set (ITU-T H.264 section 7.3.2.1.1 and annex E).
 *
 * @property width picture width in pixels after cropping.
 * @property framesPerSecond the nominal rate from the VUI timing information, or null when the
 *   stream does not state one. A raw elementary stream has no other notion of time.
 * @property fullRange `video_full_range_flag`, or null when the stream does not signal it.
 * @property matrixCoefficients the VUI `matrix_coefficients` code (1 is BT.709), or null when absent.
 */
internal data class SequenceParameterSet(
    val profileIdc: Int,
    val constraintFlags: Int,
    val levelIdc: Int,
    val width: Int,
    val height: Int,
    val interlaced: Boolean,
    val maxReferenceFrames: Int,
    val framesPerSecond: Double?,
    val fullRange: Boolean?,
    val matrixCoefficients: Int?,
) {
    val profileName: String
        get() = when (profileIdc) {
            66 -> if (constraintFlags and 0x40 != 0) "Constrained Baseline" else "Baseline"
            77 -> "Main"
            88 -> "Extended"
            100 -> "High"
            110 -> "High 10"
            122 -> "High 4:2:2"
            244 -> "High 4:4:4 Predictive"
            else -> "profile_idc $profileIdc"
        }

    /** For example "5.1"; level 1b is reported as "1b". */
    val levelName: String
        get() = if (levelIdc == 11 && constraintFlags and 0x10 != 0 && profileIdc in intArrayOf(66, 77, 88)) {
            "1b"
        } else if (levelIdc % 10 == 0) {
            "${levelIdc / 10}"
        } else {
            "${levelIdc / 10}.${levelIdc % 10}"
        }

    companion object {
        private val PROFILES_WITH_CHROMA_INFO = intArrayOf(100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135)
        private const val EXTENDED_SAR = 255

        /**
         * @param nal the SPS NAL unit starting at its header byte, without a start code.
         * @throws MalformedStreamException if it is not an SPS or ends early.
         */
        fun parse(nal: ByteArray): SequenceParameterSet {
            if (nal.isEmpty() || NalType.of(nal[0]) != NalType.SPS) {
                throw MalformedStreamException("Expected a sequence parameter set.")
            }

            val bits = BitReader(removeEmulationPrevention(nal, from = 1))
            val profileIdc = bits.readBits(8)
            val constraintFlags = bits.readBits(8)
            val levelIdc = bits.readBits(8)
            bits.readUnsignedExpGolomb() // seq_parameter_set_id

            var chromaFormatIdc = 1
            var separateColourPlanes = false
            if (profileIdc in PROFILES_WITH_CHROMA_INFO) {
                chromaFormatIdc = bits.readUnsignedExpGolomb()
                if (chromaFormatIdc == 3) separateColourPlanes = bits.readBit()
                bits.readUnsignedExpGolomb() // bit_depth_luma_minus8
                bits.readUnsignedExpGolomb() // bit_depth_chroma_minus8
                bits.readBit() // qpprime_y_zero_transform_bypass_flag
                if (bits.readBit()) { // seq_scaling_matrix_present_flag
                    repeat(if (chromaFormatIdc != 3) 8 else 12) { index ->
                        if (bits.readBit()) skipScalingList(bits, if (index < 6) 16 else 64)
                    }
                }
            }

            bits.readUnsignedExpGolomb() // log2_max_frame_num_minus4
            when (bits.readUnsignedExpGolomb()) { // pic_order_cnt_type
                0 -> bits.readUnsignedExpGolomb() // log2_max_pic_order_cnt_lsb_minus4
                1 -> {
                    bits.readBit() // delta_pic_order_always_zero_flag
                    bits.readSignedExpGolomb() // offset_for_non_ref_pic
                    bits.readSignedExpGolomb() // offset_for_top_to_bottom_field
                    repeat(bits.readUnsignedExpGolomb()) { bits.readSignedExpGolomb() }
                }
            }

            val maxReferenceFrames = bits.readUnsignedExpGolomb()
            bits.readBit() // gaps_in_frame_num_value_allowed_flag
            val widthInMacroblocks = bits.readUnsignedExpGolomb() + 1
            val heightInMapUnits = bits.readUnsignedExpGolomb() + 1
            val frameMbsOnly = bits.readBit()
            if (!frameMbsOnly) bits.readBit() // mb_adaptive_frame_field_flag
            bits.readBit() // direct_8x8_inference_flag

            var cropLeft = 0
            var cropRight = 0
            var cropTop = 0
            var cropBottom = 0
            if (bits.readBit()) { // frame_cropping_flag
                cropLeft = bits.readUnsignedExpGolomb()
                cropRight = bits.readUnsignedExpGolomb()
                cropTop = bits.readUnsignedExpGolomb()
                cropBottom = bits.readUnsignedExpGolomb()
            }

            // Cropping is counted in chroma samples, and in field rows for interlaced streams.
            val frameHeightFactor = if (frameMbsOnly) 1 else 2
            val monochromeOrSeparate = chromaFormatIdc == 0 || separateColourPlanes
            val cropUnitX = if (monochromeOrSeparate || chromaFormatIdc == 3) 1 else 2
            val cropUnitY = (if (monochromeOrSeparate || chromaFormatIdc != 1) 1 else 2) * frameHeightFactor
            val width = widthInMacroblocks * 16 - cropUnitX * (cropLeft + cropRight)
            val height = heightInMapUnits * 16 * frameHeightFactor - cropUnitY * (cropTop + cropBottom)
            if (width <= 0 || height <= 0) {
                throw MalformedStreamException("The sequence parameter set describes a ${width}x$height picture.")
            }

            var framesPerSecond: Double? = null
            var fullRange: Boolean? = null
            var matrixCoefficients: Int? = null
            if (bits.readBit()) { // vui_parameters_present_flag
                if (bits.readBit()) { // aspect_ratio_info_present_flag
                    if (bits.readBits(8) == EXTENDED_SAR) bits.skipBits(32)
                }
                if (bits.readBit()) bits.readBit() // overscan_info_present_flag, overscan_appropriate_flag
                if (bits.readBit()) { // video_signal_type_present_flag
                    bits.skipBits(3) // video_format
                    fullRange = bits.readBit()
                    if (bits.readBit()) { // colour_description_present_flag
                        bits.skipBits(16) // colour_primaries, transfer_characteristics
                        matrixCoefficients = bits.readBits(8)
                    }
                }
                if (bits.readBit()) { // chroma_loc_info_present_flag
                    bits.readUnsignedExpGolomb()
                    bits.readUnsignedExpGolomb()
                }
                if (bits.readBit()) { // timing_info_present_flag
                    val unitsInTick = bits.readLongBits(32)
                    val timeScale = bits.readLongBits(32)
                    // One tick is one field, so a frame is two ticks.
                    if (unitsInTick > 0 && timeScale > 0) framesPerSecond = timeScale / (2.0 * unitsInTick)
                }
            }

            return SequenceParameterSet(
                profileIdc = profileIdc,
                constraintFlags = constraintFlags,
                levelIdc = levelIdc,
                width = width,
                height = height,
                interlaced = !frameMbsOnly,
                maxReferenceFrames = maxReferenceFrames,
                framesPerSecond = framesPerSecond,
                fullRange = fullRange,
                matrixCoefficients = matrixCoefficients,
            )
        }

        private fun skipScalingList(bits: BitReader, size: Int) {
            var lastScale = 8
            var nextScale = 8
            repeat(size) {
                if (nextScale != 0) {
                    nextScale = (lastScale + bits.readSignedExpGolomb() + 256) % 256
                }
                if (nextScale != 0) lastScale = nextScale
            }
        }

        /**
         * Inside a NAL unit, `00 00 03` stands for `00 00`: the 03 is inserted so that the payload
         * can never look like a start code. Returns the payload with those bytes removed.
         */
        internal fun removeEmulationPrevention(nal: ByteArray, from: Int): ByteArray {
            val out = ByteArray(nal.size - from)
            var length = 0
            var zeros = 0
            for (i in from until nal.size) {
                val value = nal[i].toInt()
                if (zeros >= 2 && value == 3) {
                    zeros = 0
                    continue
                }
                zeros = if (value == 0) zeros + 1 else 0
                out[length++] = nal[i]
            }
            return out.copyOf(length)
        }
    }
}

/** Reads big-endian bit fields and Exp-Golomb codes, failing cleanly when the data runs out. */
internal class BitReader(private val data: ByteArray) {
    private var position = 0

    fun readBit(): Boolean {
        if (position >= data.size * 8) {
            throw MalformedStreamException("The sequence parameter set ends before all of its fields.")
        }
        val bit = (data[position ushr 3].toInt() ushr (7 - (position and 7))) and 1
        position++
        return bit == 1
    }

    fun readBits(count: Int): Int = readLongBits(count).toInt()

    fun readLongBits(count: Int): Long {
        require(count in 0..32)
        var value = 0L
        repeat(count) { value = (value shl 1) or (if (readBit()) 1L else 0L) }
        return value
    }

    fun skipBits(count: Int) {
        repeat(count) { readBit() }
    }

    /** ue(v): a run of n zeros, a one, then n more bits. */
    fun readUnsignedExpGolomb(): Int {
        var leadingZeros = 0
        while (!readBit()) {
            if (++leadingZeros > 31) throw MalformedStreamException("The sequence parameter set contains an invalid Exp-Golomb code.")
        }
        return ((1L shl leadingZeros) - 1 + readLongBits(leadingZeros)).toInt()
    }

    /** se(v): the unsigned code k maps to 0, 1, -1, 2, -2, ... */
    fun readSignedExpGolomb(): Int {
        val code = readUnsignedExpGolomb()
        return if (code and 1 == 1) (code + 1) / 2 else -(code / 2)
    }
}
