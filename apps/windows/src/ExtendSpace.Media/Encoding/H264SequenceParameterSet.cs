using System.Globalization;

namespace ExtendSpace.Media.Encoding;

/// <summary>
/// What an encoder actually produced, read from an H.264 sequence parameter set (ITU-T H.264 section
/// 7.3.2.1.1 and annex E). The same reading as the Android client's <c>SequenceParameterSet</c>, so
/// that both ends agree on what a stream is; used to check the encoder's output against the
/// configuration the client was told about.
/// </summary>
/// <param name="Width">Picture width in pixels after cropping.</param>
/// <param name="FramesPerSecond">The nominal rate from the VUI timing information; null when the stream does not state one.</param>
internal sealed record H264SequenceParameterSet(int ProfileIdc, int ConstraintFlags, int LevelIdc, int Width, int Height, bool Interlaced, double? FramesPerSecond)
{
    public const int HighProfileIdc = 100;
    public const int MainProfileIdc = 77;
    public const int BaselineProfileIdc = 66;

    private static readonly int[] ProfilesWithChromaInfo = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];
    private const int ExtendedSar = 255;

    public string ProfileName => ProfileIdc switch
    {
        BaselineProfileIdc => (ConstraintFlags & 0x40) != 0 ? "Constrained Baseline" : "Baseline",
        MainProfileIdc => "Main",
        88 => "Extended",
        HighProfileIdc => "High",
        _ => $"profile_idc {ProfileIdc}",
    };

    public string LevelName => LevelIdc % 10 == 0 ? $"{LevelIdc / 10}" : $"{LevelIdc / 10}.{LevelIdc % 10}";

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Width} x {Height}, {ProfileName} profile, level {LevelName}{(Interlaced ? ", interlaced" : string.Empty)}, {(FramesPerSecond is { } rate ? $"{rate:0.##} fps signalled" : "no frame rate signalled")}");

    /// <summary>The SPS of an access unit, or null if it carries none.</summary>
    /// <exception cref="FormatException">The access unit's SPS cannot be read.</exception>
    public static H264SequenceParameterSet? FindIn(ReadOnlySpan<byte> accessUnit)
    {
        foreach (Core.Protocol.NalUnit unit in Core.Protocol.AnnexB.FindNalUnits(accessUnit))
        {
            if (Core.Protocol.AnnexB.NalType(accessUnit[unit.Offset]) == Core.Protocol.H264NalType.Sps)
            {
                return Parse(accessUnit.Slice(unit.Offset, unit.Length));
            }
        }

        return null;
    }

    /// <param name="nal">The SPS NAL unit from its header byte, without a start code.</param>
    /// <exception cref="FormatException">It is not an SPS, or ends early.</exception>
    public static H264SequenceParameterSet Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty || (nal[0] & 0x1F) != Core.Protocol.H264NalType.Sps)
        {
            throw new FormatException("Expected a sequence parameter set.");
        }

        var bits = new BitReader(RemoveEmulationPrevention(nal[1..]));
        int profileIdc = bits.ReadBits(8);
        int constraintFlags = bits.ReadBits(8);
        int levelIdc = bits.ReadBits(8);
        bits.ReadUnsignedExpGolomb(); // seq_parameter_set_id

        int chromaFormatIdc = 1;
        bool separateColourPlanes = false;
        if (ProfilesWithChromaInfo.Contains(profileIdc))
        {
            chromaFormatIdc = bits.ReadUnsignedExpGolomb();
            if (chromaFormatIdc == 3)
            {
                separateColourPlanes = bits.ReadBit();
            }

            bits.ReadUnsignedExpGolomb(); // bit_depth_luma_minus8
            bits.ReadUnsignedExpGolomb(); // bit_depth_chroma_minus8
            bits.ReadBit(); // qpprime_y_zero_transform_bypass_flag
            if (bits.ReadBit())
            {
                // seq_scaling_matrix_present_flag
                for (int index = 0; index < (chromaFormatIdc != 3 ? 8 : 12); index++)
                {
                    if (bits.ReadBit())
                    {
                        SkipScalingList(bits, index < 6 ? 16 : 64);
                    }
                }
            }
        }

        bits.ReadUnsignedExpGolomb(); // log2_max_frame_num_minus4
        switch (bits.ReadUnsignedExpGolomb())
        {
            // pic_order_cnt_type
            case 0:
                bits.ReadUnsignedExpGolomb(); // log2_max_pic_order_cnt_lsb_minus4
                break;
            case 1:
                bits.ReadBit(); // delta_pic_order_always_zero_flag
                bits.ReadSignedExpGolomb(); // offset_for_non_ref_pic
                bits.ReadSignedExpGolomb(); // offset_for_top_to_bottom_field
                int cycle = bits.ReadUnsignedExpGolomb();
                for (int i = 0; i < cycle; i++)
                {
                    bits.ReadSignedExpGolomb();
                }

                break;
        }

        bits.ReadUnsignedExpGolomb(); // max_num_ref_frames
        bits.ReadBit(); // gaps_in_frame_num_value_allowed_flag
        int widthInMacroblocks = bits.ReadUnsignedExpGolomb() + 1;
        int heightInMapUnits = bits.ReadUnsignedExpGolomb() + 1;
        bool frameMbsOnly = bits.ReadBit();
        if (!frameMbsOnly)
        {
            bits.ReadBit(); // mb_adaptive_frame_field_flag
        }

        bits.ReadBit(); // direct_8x8_inference_flag
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (bits.ReadBit())
        {
            // frame_cropping_flag
            cropLeft = bits.ReadUnsignedExpGolomb();
            cropRight = bits.ReadUnsignedExpGolomb();
            cropTop = bits.ReadUnsignedExpGolomb();
            cropBottom = bits.ReadUnsignedExpGolomb();
        }

        // Cropping is counted in chroma samples, and in field rows for interlaced streams.
        int frameHeightFactor = frameMbsOnly ? 1 : 2;
        bool monochromeOrSeparate = chromaFormatIdc == 0 || separateColourPlanes;
        int cropUnitX = monochromeOrSeparate || chromaFormatIdc == 3 ? 1 : 2;
        int cropUnitY = (monochromeOrSeparate || chromaFormatIdc != 1 ? 1 : 2) * frameHeightFactor;
        int width = (widthInMacroblocks * 16) - (cropUnitX * (cropLeft + cropRight));
        int height = (heightInMapUnits * 16 * frameHeightFactor) - (cropUnitY * (cropTop + cropBottom));
        if (width <= 0 || height <= 0)
        {
            throw new FormatException($"The sequence parameter set describes a {width} x {height} picture.");
        }

        double? framesPerSecond = null;
        if (bits.ReadBit())
        {
            // vui_parameters_present_flag
            if (bits.ReadBit() && bits.ReadBits(8) == ExtendedSar)
            {
                bits.SkipBits(32);
            }

            if (bits.ReadBit())
            {
                bits.ReadBit(); // overscan_appropriate_flag
            }

            if (bits.ReadBit())
            {
                // video_signal_type_present_flag: video_format, full range, then the colour description
                bits.SkipBits(4);
                if (bits.ReadBit())
                {
                    bits.SkipBits(24);
                }
            }

            if (bits.ReadBit())
            {
                // chroma_loc_info_present_flag
                bits.ReadUnsignedExpGolomb();
                bits.ReadUnsignedExpGolomb();
            }

            if (bits.ReadBit())
            {
                // timing_info_present_flag. One tick is one field, so a frame is two ticks.
                long unitsInTick = bits.ReadLongBits(32);
                long timeScale = bits.ReadLongBits(32);
                if (unitsInTick > 0 && timeScale > 0)
                {
                    framesPerSecond = timeScale / (2.0 * unitsInTick);
                }
            }
        }

        return new H264SequenceParameterSet(profileIdc, constraintFlags, levelIdc, width, height, !frameMbsOnly, framesPerSecond);
    }

    private static void SkipScalingList(BitReader bits, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                nextScale = (lastScale + bits.ReadSignedExpGolomb() + 256) % 256;
            }

            if (nextScale != 0)
            {
                lastScale = nextScale;
            }
        }
    }

    /// <summary>Inside a NAL unit, <c>00 00 03</c> stands for <c>00 00</c>; the 03 is removed.</summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> payload)
    {
        var output = new byte[payload.Length];
        int length = 0;
        int zeros = 0;
        foreach (byte value in payload)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = value == 0 ? zeros + 1 : 0;
            output[length++] = value;
        }

        return output[..length];
    }

    private sealed class BitReader(byte[] data)
    {
        private long _position;

        public bool ReadBit()
        {
            if (_position >= data.LongLength * 8)
            {
                throw new FormatException("The sequence parameter set ends early.");
            }

            bool bit = ((data[_position >> 3] >> (7 - (int)(_position & 7))) & 1) != 0;
            _position++;
            return bit;
        }

        public int ReadBits(int count) => (int)ReadLongBits(count);

        public long ReadLongBits(int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | (ReadBit() ? 1L : 0L);
            }

            return value;
        }

        public void SkipBits(int count) => ReadLongBits(count);

        public int ReadUnsignedExpGolomb()
        {
            int leadingZeros = 0;
            while (!ReadBit())
            {
                if (++leadingZeros > 31)
                {
                    throw new FormatException("An Exp-Golomb code in the sequence parameter set is too long.");
                }
            }

            return (int)((1L << leadingZeros) - 1 + ReadLongBits(leadingZeros));
        }

        public int ReadSignedExpGolomb()
        {
            int code = ReadUnsignedExpGolomb();
            return (code & 1) != 0 ? (code + 1) / 2 : -(code / 2);
        }
    }
}
