namespace ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

/// <summary>
/// Hand-built Annex B fragments. The NAL payloads are placeholders: these tests are about framing,
/// which depends only on start codes and the NAL header byte.
/// </summary>
internal static class H264Samples
{
    public static readonly byte[] StartCode4 = [0, 0, 0, 1];
    public static readonly byte[] StartCode3 = [0, 0, 1];

    /// <summary>SPS header byte, then profile_idc 100 (High), constraint flags 0, level_idc 51.</summary>
    public static readonly byte[] Sps = [0x67, 100, 0x00, 51, 0xAC, 0x2B];

    public static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0x80];

    public static readonly byte[] AccessUnitDelimiter = [0x09, 0x10];

    public static readonly byte[] Sei = [0x06, 0x05, 0x01, 0xAA, 0x80];

    public static readonly byte[] IdrSlice = [0x65, 0x88, 0x84, 0x21, 0xA0];

    public static readonly byte[] NonIdrSlice = [0x41, 0x9A, 0x24, 0x6C, 0x41];

    /// <summary>An IDR access unit laid out the way hardware encoders emit them.</summary>
    public static byte[] IdrAccessUnit => Join(AccessUnitDelimiter, Sps, Pps, Sei, IdrSlice);

    public static byte[] NonIdrAccessUnit => Join(AccessUnitDelimiter, NonIdrSlice);

    /// <summary>Concatenates NAL units, each behind a four-byte start code.</summary>
    public static byte[] Join(params byte[][] nalUnits) =>
        [.. nalUnits.SelectMany(unit => StartCode4.Concat(unit))];
}
