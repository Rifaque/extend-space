using ExtendSpace.Experiments.CaptureEncode.Bitstream;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

public class H264StreamStatisticsTests
{
    [Fact]
    public void An_idr_access_unit_with_its_parameter_sets_is_recognised()
    {
        var statistics = new H264StreamStatistics();

        AccessUnitInfo info = statistics.AddAccessUnit(H264Samples.IdrAccessUnit);

        Assert.True(info.StartsWithStartCode);
        Assert.True(info.IsIdr);
        Assert.True(info.HasSps);
        Assert.True(info.HasPps);
        Assert.True(info.ParameterSetsPrecedeIdr);
        Assert.Equal(1, info.VclNalUnits);

        Assert.Equal(1, statistics.AccessUnits);
        Assert.Equal(1, statistics.IdrAccessUnits);
        Assert.Equal(1, statistics.IdrAccessUnitsWithParameterSets);
        Assert.Equal(0, statistics.NonIdrAccessUnits);
        Assert.Equal(0, statistics.MalformedAccessUnits);
        Assert.Equal(1, statistics.SpsNalUnits);
        Assert.Equal(1, statistics.PpsNalUnits);
        Assert.Equal(1, statistics.SeiNalUnits);
        Assert.Equal(1, statistics.AccessUnitDelimiters);
        Assert.Equal(1, statistics.SliceNalUnits);
        Assert.Equal(H264Samples.IdrAccessUnit.Length, statistics.TotalBytes);
        Assert.True(statistics.HasParameterSets);
        Assert.True(statistics.StartsWithDecodableKeyframe);
    }

    [Fact]
    public void A_stream_is_counted_access_unit_by_access_unit()
    {
        var statistics = new H264StreamStatistics();

        statistics.AddAccessUnit(H264Samples.IdrAccessUnit);
        statistics.AddAccessUnit(H264Samples.NonIdrAccessUnit);
        statistics.AddAccessUnit(H264Samples.NonIdrAccessUnit);
        statistics.AddAccessUnit(H264Samples.IdrAccessUnit);

        Assert.Equal(4, statistics.AccessUnits);
        Assert.Equal(2, statistics.IdrAccessUnits);
        Assert.Equal(2, statistics.IdrAccessUnitsWithParameterSets);
        Assert.Equal(2, statistics.NonIdrAccessUnits);
        Assert.Equal(2, statistics.SpsNalUnits);
        Assert.Equal(4, statistics.AccessUnitDelimiters);
        Assert.Equal(
            2 * (H264Samples.IdrAccessUnit.Length + H264Samples.NonIdrAccessUnit.Length),
            statistics.TotalBytes);
    }

    [Fact]
    public void An_idr_without_parameter_sets_is_not_counted_as_carrying_them()
    {
        var statistics = new H264StreamStatistics();

        AccessUnitInfo info = statistics.AddAccessUnit(H264Samples.Join(H264Samples.IdrSlice));

        Assert.True(info.IsIdr);
        Assert.False(info.ParameterSetsPrecedeIdr);
        Assert.Equal(1, statistics.IdrAccessUnits);
        Assert.Equal(0, statistics.IdrAccessUnitsWithParameterSets);
        Assert.False(statistics.HasParameterSets);
        Assert.False(statistics.StartsWithDecodableKeyframe);
    }

    [Fact]
    public void Parameter_sets_after_the_picture_do_not_count_as_preceding_it()
    {
        var statistics = new H264StreamStatistics();

        AccessUnitInfo info = statistics.AddAccessUnit(H264Samples.Join(H264Samples.IdrSlice, H264Samples.Sps, H264Samples.Pps));

        Assert.True(info.HasSps);
        Assert.True(info.HasPps);
        Assert.False(info.ParameterSetsPrecedeIdr);
        Assert.Equal(0, statistics.IdrAccessUnitsWithParameterSets);
    }

    [Fact]
    public void A_stream_that_starts_on_a_non_idr_picture_is_flagged()
    {
        var statistics = new H264StreamStatistics();

        statistics.AddAccessUnit(H264Samples.NonIdrAccessUnit);
        statistics.AddAccessUnit(H264Samples.IdrAccessUnit);

        Assert.False(statistics.StartsWithDecodableKeyframe);
    }

    [Fact]
    public void How_a_stream_starts_is_unknown_until_something_is_written()
    {
        Assert.Null(new H264StreamStatistics().StartsWithDecodableKeyframe);
    }

    [Fact]
    public void Multiple_slices_make_one_access_unit()
    {
        var statistics = new H264StreamStatistics();

        AccessUnitInfo info = statistics.AddAccessUnit(
            H264Samples.Join(H264Samples.NonIdrSlice, H264Samples.NonIdrSlice, H264Samples.NonIdrSlice));

        Assert.Equal(3, info.VclNalUnits);
        Assert.Equal(1, statistics.AccessUnits);
        Assert.Equal(1, statistics.NonIdrAccessUnits);
        Assert.Equal(3, statistics.SliceNalUnits);
    }

    [Fact]
    public void A_sample_that_does_not_begin_with_a_start_code_is_malformed()
    {
        var statistics = new H264StreamStatistics();

        // Length-prefixed (AVCC) data, as found inside MP4, is the usual way to get this wrong.
        AccessUnitInfo info = statistics.AddAccessUnit([0, 0, 0, 5, .. H264Samples.IdrSlice]);

        Assert.False(info.StartsWithStartCode);
        Assert.Equal(1, statistics.MalformedAccessUnits);
        Assert.Equal(0, statistics.IdrAccessUnits);
    }

    [Fact]
    public void A_sample_with_no_picture_data_is_malformed()
    {
        var statistics = new H264StreamStatistics();

        statistics.AddAccessUnit(H264Samples.Join(H264Samples.Sps, H264Samples.Pps));

        Assert.Equal(1, statistics.MalformedAccessUnits);
        Assert.Equal(0, statistics.IdrAccessUnits + statistics.NonIdrAccessUnits);
    }

    [Fact]
    public void Profile_and_level_come_from_the_first_sps()
    {
        var statistics = new H264StreamStatistics();
        Assert.Null(statistics.ProfileIdc);

        statistics.AddAccessUnit(H264Samples.IdrAccessUnit);
        statistics.AddAccessUnit(H264Samples.Join([0x67, 66, 0xC0, 31, 0x8D], H264Samples.Pps, H264Samples.IdrSlice));

        Assert.Equal(100, statistics.ProfileIdc);
        Assert.Equal(0x00, statistics.ConstraintFlags);
        Assert.Equal(51, statistics.LevelIdc);
    }

    [Fact]
    public void A_truncated_sps_yields_no_profile()
    {
        var statistics = new H264StreamStatistics();

        statistics.AddAccessUnit(H264Samples.Join([0x67, 100], H264Samples.Pps, H264Samples.IdrSlice));

        Assert.Equal(1, statistics.SpsNalUnits);
        Assert.Null(statistics.ProfileIdc);
        Assert.Null(statistics.LevelIdc);
    }

    [Fact]
    public void Separately_written_parameter_sets_count_as_bytes_and_nal_units_but_not_as_an_access_unit()
    {
        var statistics = new H264StreamStatistics();
        byte[] header = H264Samples.Join(H264Samples.Sps, H264Samples.Pps);

        statistics.AddParameterSets(header);

        Assert.Equal(0, statistics.AccessUnits);
        Assert.Equal(header.Length, statistics.TotalBytes);
        Assert.True(statistics.HasParameterSets);
        Assert.Equal(100, statistics.ProfileIdc);
        Assert.Null(statistics.StartsWithDecodableKeyframe);
    }
}
