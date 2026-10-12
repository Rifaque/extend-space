package com.rsxdevelops.extendspace.decodertest.h264

import android.media.MediaCodecInfo.CodecProfileLevel

/**
 * Translates what an H.264 stream says about itself into the constants Android's codec list uses,
 * so that a stream can be compared with what a decoder reports it supports.
 */
internal object AvcProfileLevel {
    private const val CONSTRAINT_SET_1 = 0x40
    private const val CONSTRAINT_SET_3 = 0x10
    private const val CONSTRAINT_SET_4 = 0x08
    private const val CONSTRAINT_SET_5 = 0x04

    /** The `CodecProfileLevel.AVCProfile*` constant, or null for a profile Android has no constant for. */
    fun profile(sps: SequenceParameterSet): Int? = when (sps.profileIdc) {
        66 -> if (sps.constraintFlags and CONSTRAINT_SET_1 != 0) {
            CodecProfileLevel.AVCProfileConstrainedBaseline
        } else {
            CodecProfileLevel.AVCProfileBaseline
        }
        77 -> CodecProfileLevel.AVCProfileMain
        88 -> CodecProfileLevel.AVCProfileExtended
        100 -> {
            val constrained = CONSTRAINT_SET_4 or CONSTRAINT_SET_5
            if (sps.constraintFlags and constrained == constrained) {
                CodecProfileLevel.AVCProfileConstrainedHigh
            } else {
                CodecProfileLevel.AVCProfileHigh
            }
        }
        110 -> CodecProfileLevel.AVCProfileHigh10
        122 -> CodecProfileLevel.AVCProfileHigh422
        244 -> CodecProfileLevel.AVCProfileHigh444
        else -> null
    }

    /** The `CodecProfileLevel.AVCLevel*` constant, or null for a `level_idc` the standard does not define. */
    fun level(sps: SequenceParameterSet): Int? = when (sps.levelIdc) {
        9 -> CodecProfileLevel.AVCLevel1b
        10 -> CodecProfileLevel.AVCLevel1
        11 -> if (sps.constraintFlags and CONSTRAINT_SET_3 != 0 && sps.profileIdc in intArrayOf(66, 77, 88)) {
            CodecProfileLevel.AVCLevel1b
        } else {
            CodecProfileLevel.AVCLevel11
        }
        12 -> CodecProfileLevel.AVCLevel12
        13 -> CodecProfileLevel.AVCLevel13
        20 -> CodecProfileLevel.AVCLevel2
        21 -> CodecProfileLevel.AVCLevel21
        22 -> CodecProfileLevel.AVCLevel22
        30 -> CodecProfileLevel.AVCLevel3
        31 -> CodecProfileLevel.AVCLevel31
        32 -> CodecProfileLevel.AVCLevel32
        40 -> CodecProfileLevel.AVCLevel4
        41 -> CodecProfileLevel.AVCLevel41
        42 -> CodecProfileLevel.AVCLevel42
        50 -> CodecProfileLevel.AVCLevel5
        51 -> CodecProfileLevel.AVCLevel51
        52 -> CodecProfileLevel.AVCLevel52
        60 -> CodecProfileLevel.AVCLevel6
        61 -> CodecProfileLevel.AVCLevel61
        62 -> CodecProfileLevel.AVCLevel62
        else -> null
    }

    /**
     * Whether a decoder that lists [supported] profile and level pairs covers the stream. The
     * Android level constants grow with the level, so "at least this level" is a plain comparison.
     */
    fun isCovered(sps: SequenceParameterSet, supported: List<Pair<Int, Int>>): Boolean {
        val profile = profile(sps) ?: return false
        val level = level(sps) ?: return false
        return supported.any { (supportedProfile, maxLevel) -> supportedProfile == profile && maxLevel >= level }
    }

    fun profileName(profile: Int): String = when (profile) {
        CodecProfileLevel.AVCProfileBaseline -> "Baseline"
        CodecProfileLevel.AVCProfileConstrainedBaseline -> "Constrained Baseline"
        CodecProfileLevel.AVCProfileMain -> "Main"
        CodecProfileLevel.AVCProfileExtended -> "Extended"
        CodecProfileLevel.AVCProfileHigh -> "High"
        CodecProfileLevel.AVCProfileConstrainedHigh -> "Constrained High"
        CodecProfileLevel.AVCProfileHigh10 -> "High 10"
        CodecProfileLevel.AVCProfileHigh422 -> "High 4:2:2"
        CodecProfileLevel.AVCProfileHigh444 -> "High 4:4:4"
        else -> "profile 0x${profile.toString(16)}"
    }

    fun levelName(level: Int): String = when (level) {
        CodecProfileLevel.AVCLevel1 -> "1"
        CodecProfileLevel.AVCLevel1b -> "1b"
        CodecProfileLevel.AVCLevel11 -> "1.1"
        CodecProfileLevel.AVCLevel12 -> "1.2"
        CodecProfileLevel.AVCLevel13 -> "1.3"
        CodecProfileLevel.AVCLevel2 -> "2"
        CodecProfileLevel.AVCLevel21 -> "2.1"
        CodecProfileLevel.AVCLevel22 -> "2.2"
        CodecProfileLevel.AVCLevel3 -> "3"
        CodecProfileLevel.AVCLevel31 -> "3.1"
        CodecProfileLevel.AVCLevel32 -> "3.2"
        CodecProfileLevel.AVCLevel4 -> "4"
        CodecProfileLevel.AVCLevel41 -> "4.1"
        CodecProfileLevel.AVCLevel42 -> "4.2"
        CodecProfileLevel.AVCLevel5 -> "5"
        CodecProfileLevel.AVCLevel51 -> "5.1"
        CodecProfileLevel.AVCLevel52 -> "5.2"
        CodecProfileLevel.AVCLevel6 -> "6"
        CodecProfileLevel.AVCLevel61 -> "6.1"
        CodecProfileLevel.AVCLevel62 -> "6.2"
        else -> "level 0x${level.toString(16)}"
    }
}
