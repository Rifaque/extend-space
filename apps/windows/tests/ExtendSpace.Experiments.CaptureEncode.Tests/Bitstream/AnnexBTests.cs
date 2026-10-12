using ExtendSpace.Experiments.CaptureEncode.Bitstream;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

public class AnnexBTests
{
    [Fact]
    public void Four_byte_start_codes_delimit_nal_units()
    {
        byte[] data = H264Samples.Join(H264Samples.Sps, H264Samples.Pps, H264Samples.IdrSlice);

        List<NalUnit> units = AnnexB.FindNalUnits(data, out int leadingBytes);

        Assert.Equal(0, leadingBytes);
        Assert.Equal(3, units.Count);
        Assert.All(units, unit => Assert.Equal(4, unit.StartCodeLength));
        Assert.Equal(H264Samples.Sps, Slice(data, units[0]));
        Assert.Equal(H264Samples.Pps, Slice(data, units[1]));
        Assert.Equal(H264Samples.IdrSlice, Slice(data, units[2]));
    }

    [Fact]
    public void Three_byte_start_codes_delimit_nal_units()
    {
        byte[] data = [.. H264Samples.StartCode3, .. H264Samples.Sps, .. H264Samples.StartCode3, .. H264Samples.NonIdrSlice];

        List<NalUnit> units = AnnexB.FindNalUnits(data);

        Assert.Equal(2, units.Count);
        Assert.All(units, unit => Assert.Equal(3, unit.StartCodeLength));
        Assert.Equal(H264Samples.Sps, Slice(data, units[0]));
        Assert.Equal(H264Samples.NonIdrSlice, Slice(data, units[1]));
    }

    [Fact]
    public void Mixed_start_code_lengths_are_told_apart()
    {
        byte[] data = [.. H264Samples.StartCode4, .. H264Samples.Sps, .. H264Samples.StartCode3, .. H264Samples.Pps];

        List<NalUnit> units = AnnexB.FindNalUnits(data);

        Assert.Equal([4, 3], units.Select(unit => unit.StartCodeLength));
        Assert.Equal(H264Samples.Sps, Slice(data, units[0]));
        Assert.Equal(H264Samples.Pps, Slice(data, units[1]));
    }

    [Fact]
    public void Trailing_zero_padding_is_not_part_of_a_nal_unit()
    {
        byte[] data = [.. H264Samples.StartCode4, .. H264Samples.Pps, 0, 0, 0, 0, 0];

        NalUnit unit = Assert.Single(AnnexB.FindNalUnits(data));

        Assert.Equal(H264Samples.Pps, Slice(data, unit));
    }

    [Fact]
    public void The_nal_type_is_the_low_five_bits_of_the_header()
    {
        Assert.Equal(H264NalType.Sps, AnnexB.NalType(0x67));
        Assert.Equal(H264NalType.Sps, AnnexB.NalType(0x27));
        Assert.Equal(H264NalType.Pps, AnnexB.NalType(0x68));
        Assert.Equal(H264NalType.IdrSlice, AnnexB.NalType(0x65));
        Assert.Equal(H264NalType.NonIdrSlice, AnnexB.NalType(0x41));
        Assert.Equal(H264NalType.NonIdrSlice, AnnexB.NalType(0x01));
    }

    [Fact]
    public void Bytes_ahead_of_the_first_start_code_are_reported()
    {
        byte[] data = [0xDE, 0xAD, .. H264Samples.StartCode3, .. H264Samples.Sps];

        List<NalUnit> units = AnnexB.FindNalUnits(data, out int leadingBytes);

        Assert.Equal(2, leadingBytes);
        Assert.Single(units);
    }

    [Fact]
    public void Leading_zero_bytes_are_padding_not_garbage()
    {
        byte[] data = [0, 0, 0, .. H264Samples.StartCode4, .. H264Samples.Sps];

        AnnexB.FindNalUnits(data, out int leadingBytes);

        Assert.Equal(0, leadingBytes);
    }

    [Theory]
    [InlineData(new byte[0], 0)]
    [InlineData(new byte[] { 0, 0 }, 0)]
    [InlineData(new byte[] { 0x65, 0x88, 0x84 }, 3)]
    public void Data_without_any_start_code_contains_no_nal_units(byte[] data, int expectedLeadingBytes)
    {
        List<NalUnit> units = AnnexB.FindNalUnits(data, out int leadingBytes);

        Assert.Empty(units);
        Assert.Equal(expectedLeadingBytes, leadingBytes);
    }

    [Fact]
    public void A_start_code_with_nothing_after_it_is_not_a_nal_unit()
    {
        Assert.Empty(AnnexB.FindNalUnits([0, 0, 0, 1]));
        Assert.Single(AnnexB.FindNalUnits([0, 0, 1, 0, 0, 1, 0x68, 0x01]));
    }

    private static byte[] Slice(byte[] data, NalUnit unit) => data.AsSpan(unit.Offset, unit.Length).ToArray();
}
