using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Measurement;

public class DurationSeriesTests
{
    [Fact]
    public void Nothing_recorded_is_reported_as_no_measurement_not_as_zero()
    {
        Assert.Null(new DurationSeries().Summarize());
    }

    [Fact]
    public void Summary_statistics_use_the_recorded_values()
    {
        var series = new DurationSeries();
        foreach (int milliseconds in new[] { 5, 1, 4, 2, 3, 10, 6, 8, 7, 9 })
        {
            series.Add(TimeSpan.FromMilliseconds(milliseconds));
        }

        DurationSummary summary = Assert.NotNull(series.Summarize());
        Assert.Equal(10, summary.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(5.5), summary.Mean);
        Assert.Equal(TimeSpan.FromMilliseconds(1), summary.Minimum);
        Assert.Equal(TimeSpan.FromMilliseconds(5), summary.Median);
        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.Percentile95);
        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.Maximum);
    }

    [Theory]
    [InlineData(50, 2)]
    [InlineData(75, 3)]
    [InlineData(100, 4)]
    [InlineData(1, 1)]
    public void Percentiles_are_nearest_rank(double percentile, long expected)
    {
        Assert.Equal(expected, DurationSeries.Percentile([1, 2, 3, 4], percentile));
    }

    [Fact]
    public void A_single_value_is_its_own_summary()
    {
        var series = new DurationSeries();
        series.Add(TimeSpan.FromMilliseconds(7));

        DurationSummary summary = Assert.NotNull(series.Summarize());
        Assert.Equal(summary.Minimum, summary.Maximum);
        Assert.Equal(TimeSpan.FromMilliseconds(7), summary.Median);
        Assert.Equal(TimeSpan.FromMilliseconds(7), summary.Percentile95);
    }

    [Fact]
    public void Values_beyond_the_capacity_are_counted_but_not_kept()
    {
        var series = new DurationSeries(capacity: 2);
        series.Add(TimeSpan.FromMilliseconds(1));
        series.Add(TimeSpan.FromMilliseconds(2));
        series.Add(TimeSpan.FromMilliseconds(500));

        Assert.Equal(2, series.Count);
        Assert.Equal(1, series.Overflowed);
        Assert.Equal(TimeSpan.FromMilliseconds(2), Assert.NotNull(series.Summarize()).Maximum);
    }

    [Fact]
    public void The_default_capacity_covers_the_longest_permitted_run()
    {
        const int MaximumSeconds = 600;
        const int MaximumFrameRate = 240;
        Assert.True(DurationSeries.DefaultCapacity >= MaximumSeconds * MaximumFrameRate);
    }
}
