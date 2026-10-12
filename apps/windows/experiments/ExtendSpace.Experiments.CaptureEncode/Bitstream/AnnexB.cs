namespace ExtendSpace.Experiments.CaptureEncode.Bitstream;

/// <summary>One NAL unit inside an Annex B buffer, excluding its start code.</summary>
internal readonly record struct NalUnit(int Offset, int Length, int StartCodeLength);

internal static class H264NalType
{
    public const int NonIdrSlice = 1;
    public const int IdrSlice = 5;
    public const int Sei = 6;
    public const int Sps = 7;
    public const int Pps = 8;
    public const int AccessUnitDelimiter = 9;

    /// <summary>Types 1 to 5 carry coded slice data.</summary>
    public static bool IsVcl(int type) => type is >= 1 and <= 5;
}

/// <summary>Splits an H.264 Annex B byte stream (ITU-T H.264 Annex B) into NAL units.</summary>
internal static class AnnexB
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    public static List<NalUnit> FindNalUnits(ReadOnlySpan<byte> data) => FindNalUnits(data, out _);

    /// <param name="leadingBytes">
    /// Bytes before the first start code that are not zero padding. A well-formed buffer has none.
    /// </param>
    public static List<NalUnit> FindNalUnits(ReadOnlySpan<byte> data, out int leadingBytes)
    {
        var units = new List<NalUnit>();
        leadingBytes = 0;

        int payloadStart = -1;
        int startCodeLength = 0;
        int searchFrom = 0;

        while (searchFrom <= data.Length - StartCode.Length)
        {
            int relative = data[searchFrom..].IndexOf(StartCode);
            if (relative < 0)
            {
                break;
            }

            int startCode = searchFrom + relative;
            if (payloadStart >= 0)
            {
                AddUnit(data, units, payloadStart, startCode, startCodeLength);
            }
            else
            {
                leadingBytes = data[..startCode].TrimEnd((byte)0).Length;
            }

            // The four-byte form is the same code with a zero byte in front.
            bool fourByte = startCode > Math.Max(payloadStart, 0) && data[startCode - 1] == 0;
            startCodeLength = fourByte ? 4 : 3;
            payloadStart = startCode + StartCode.Length;
            searchFrom = payloadStart;
        }

        if (payloadStart >= 0)
        {
            AddUnit(data, units, payloadStart, data.Length, startCodeLength);
        }
        else
        {
            leadingBytes = data.TrimEnd((byte)0).Length;
        }

        return units;
    }

    /// <summary>The nal_unit_type field of a NAL unit's first byte.</summary>
    public static int NalType(byte header) => header & 0x1F;

    private static void AddUnit(ReadOnlySpan<byte> data, List<NalUnit> units, int start, int end, int startCodeLength)
    {
        // A NAL unit never ends in a zero byte, so trailing zeros are padding or belong to the
        // next start code.
        int length = data[start..end].TrimEnd((byte)0).Length;
        if (length > 0)
        {
            units.Add(new NalUnit(start, length, startCodeLength));
        }
    }
}
