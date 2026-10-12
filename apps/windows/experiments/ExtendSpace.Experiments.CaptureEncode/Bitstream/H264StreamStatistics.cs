namespace ExtendSpace.Experiments.CaptureEncode.Bitstream;

/// <summary>What one encoder output sample contained.</summary>
internal readonly record struct AccessUnitInfo(
    bool StartsWithStartCode,
    bool IsIdr,
    bool HasSps,
    bool HasPps,
    bool ParameterSetsPrecedeIdr,
    int VclNalUnits);

/// <summary>
/// Structural statistics of the stream, gathered from the bytes on their way to the file. This
/// checks framing only; it does not decode anything, so it cannot prove the stream is decodable.
/// </summary>
internal sealed class H264StreamStatistics
{
    public long AccessUnits { get; private set; }

    public long TotalBytes { get; private set; }

    public long IdrAccessUnits { get; private set; }

    public long NonIdrAccessUnits { get; private set; }

    /// <summary>IDR access units in which an SPS and a PPS come before the first IDR slice.</summary>
    public long IdrAccessUnitsWithParameterSets { get; private set; }

    /// <summary>Samples that do not begin with a start code or contain no slice data.</summary>
    public long MalformedAccessUnits { get; private set; }

    public long SpsNalUnits { get; private set; }

    public long PpsNalUnits { get; private set; }

    public long SeiNalUnits { get; private set; }

    public long AccessUnitDelimiters { get; private set; }

    public long SliceNalUnits { get; private set; }

    /// <summary>True once an SPS and a PPS have both been seen.</summary>
    public bool HasParameterSets => SpsNalUnits > 0 && PpsNalUnits > 0;

    /// <summary>Whether the first sample was an IDR picture carrying its own SPS and PPS.</summary>
    public bool? StartsWithDecodableKeyframe { get; private set; }

    /// <summary>profile_idc from the first SPS; null until one is seen.</summary>
    public int? ProfileIdc { get; private set; }

    /// <summary>The constraint_set flags byte from the first SPS.</summary>
    public int? ConstraintFlags { get; private set; }

    /// <summary>level_idc from the first SPS (41 means level 4.1).</summary>
    public int? LevelIdc { get; private set; }

    /// <summary>Inspects bytes that are written to the file but are not an encoder output sample.</summary>
    public void AddParameterSets(ReadOnlySpan<byte> data)
    {
        TotalBytes += data.Length;
        foreach (NalUnit unit in AnnexB.FindNalUnits(data))
        {
            CountNonVcl(data, unit);
        }
    }

    public AccessUnitInfo AddAccessUnit(ReadOnlySpan<byte> data)
    {
        List<NalUnit> units = AnnexB.FindNalUnits(data, out int leadingBytes);

        bool hasSps = false;
        bool hasPps = false;
        bool isIdr = false;
        bool parameterSetsPrecedeIdr = false;
        int vclUnits = 0;

        foreach (NalUnit unit in units)
        {
            int type = AnnexB.NalType(data[unit.Offset]);
            if (H264NalType.IsVcl(type))
            {
                vclUnits++;
                SliceNalUnits++;
                if (type == H264NalType.IdrSlice && !isIdr)
                {
                    isIdr = true;
                    parameterSetsPrecedeIdr = hasSps && hasPps;
                }

                continue;
            }

            hasSps |= type == H264NalType.Sps;
            hasPps |= type == H264NalType.Pps;
            CountNonVcl(data, unit);
        }

        bool startsWithStartCode = units.Count > 0 && leadingBytes == 0;
        var info = new AccessUnitInfo(startsWithStartCode, isIdr, hasSps, hasPps, parameterSetsPrecedeIdr, vclUnits);

        StartsWithDecodableKeyframe ??= isIdr && parameterSetsPrecedeIdr;
        AccessUnits++;
        TotalBytes += data.Length;

        if (!startsWithStartCode || vclUnits == 0)
        {
            MalformedAccessUnits++;
        }
        else if (isIdr)
        {
            IdrAccessUnits++;
            if (parameterSetsPrecedeIdr)
            {
                IdrAccessUnitsWithParameterSets++;
            }
        }
        else
        {
            NonIdrAccessUnits++;
        }

        return info;
    }

    private void CountNonVcl(ReadOnlySpan<byte> data, NalUnit unit)
    {
        switch (AnnexB.NalType(data[unit.Offset]))
        {
            case H264NalType.Sps:
                SpsNalUnits++;
                if (ProfileIdc is null && unit.Length >= 4)
                {
                    // seq_parameter_set_data starts profile_idc, constraint flags, level_idc. These
                    // three bytes cannot contain an emulation-prevention byte for any real profile.
                    ProfileIdc = data[unit.Offset + 1];
                    ConstraintFlags = data[unit.Offset + 2];
                    LevelIdc = data[unit.Offset + 3];
                }

                break;
            case H264NalType.Pps:
                PpsNalUnits++;
                break;
            case H264NalType.Sei:
                SeiNalUnits++;
                break;
            case H264NalType.AccessUnitDelimiter:
                AccessUnitDelimiters++;
                break;
        }
    }
}
