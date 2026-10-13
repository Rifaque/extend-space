using ExtendSpace.Experiments.CaptureEncode.Capture;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Capture;

public class ProbeTests
{
    [Fact]
    public void Silence_is_the_longest_run_of_seconds_in_which_windows_raised_no_event()
    {
        ProbeSecond[] seconds = [Second(60), Second(0), Second(0), Second(12), Second(0), Second(0), Second(0), Second(59)];

        Assert.Equal(3, ProbeRunner.LongestSilence(seconds));
    }

    [Fact]
    public void A_second_with_events_but_no_frames_is_not_silence()
    {
        // Windows raised the event and the pool was empty: a different thing from raising nothing.
        ProbeSecond[] seconds = [Second(60), Second(callbacks: 5, delivered: 0), Second(60)];

        Assert.Equal(0, ProbeRunner.LongestSilence(seconds));
        Assert.Equal(0, ProbeRunner.LongestSilence([]));
    }

    [Fact]
    public void A_line_tells_frames_windows_withheld_from_frames_this_program_set_aside()
    {
        string line = new ProbeSecond(12.0, 72, 0, 72, 12, 0, 60, 60, 1_500_000, 3.2).Describe();

        Assert.Contains("callbacks   72", line);
        Assert.Contains("frames   72", line);
        Assert.Contains("paced-out   12", line);
        Assert.Contains("in   60 out   60", line);
        Assert.Contains("12000 kbit", line);
        Assert.Contains("newest frame       3 ms old", line);
    }

    private static ProbeSecond Second(long callbacks, long? delivered = null) =>
        new(0, callbacks, 0, delivered ?? callbacks, 0, 0, 0, 0, 0, null);
}
