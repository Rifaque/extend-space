using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Measurement;

public class MetricsTests
{
    [Fact]
    public void Mebibytes_are_1024_squared_bytes()
    {
        Assert.Equal(1.0, Metrics.ToMebibytes(1_048_576));
        Assert.Equal(2.5, Metrics.ToMebibytes(2_621_440));
    }

    [Fact]
    public void Rates_divide_by_the_elapsed_time()
    {
        Assert.Equal(60.0, Metrics.FramesPerSecond(300, TimeSpan.FromSeconds(5)));
        Assert.Equal(8_000_000.0, Metrics.BitsPerSecond(5_000_000, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Rates_over_no_time_are_unavailable_rather_than_infinite()
    {
        Assert.Null(Metrics.FramesPerSecond(300, TimeSpan.Zero));
        Assert.Null(Metrics.BitsPerSecond(300, TimeSpan.Zero));
        Assert.Null(Metrics.CpuPercentOfOneCore(TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void Processor_use_is_relative_to_one_logical_processor()
    {
        Assert.Equal(50.0, Metrics.CpuPercentOfOneCore(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Equal(250.0, Metrics.CpuPercentOfOneCore(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
    }
}
