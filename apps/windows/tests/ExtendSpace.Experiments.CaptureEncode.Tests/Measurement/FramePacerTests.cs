using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Measurement;

public class FramePacerTests
{
    private static readonly TimeSpan SixtyFps = TimeSpan.FromSeconds(1.0 / 60);

    [Fact]
    public void The_first_frame_is_always_accepted()
    {
        var pacer = new FramePacer(SixtyFps);
        Assert.True(pacer.ShouldAccept(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void A_source_at_the_target_rate_passes_untouched()
    {
        var pacer = new FramePacer(SixtyFps);
        int accepted = CountAccepted(pacer, sourceRate: 60, seconds: 2);
        Assert.Equal(120, accepted);
    }

    [Fact]
    public void A_slower_source_passes_untouched()
    {
        var pacer = new FramePacer(SixtyFps);
        int accepted = CountAccepted(pacer, sourceRate: 24, seconds: 2);
        Assert.Equal(48, accepted);
    }

    [Theory]
    [InlineData(165)]
    [InlineData(144)]
    [InlineData(120)]
    [InlineData(75)]
    public void A_faster_source_is_thinned_to_the_target_rate_on_average(int sourceRate)
    {
        var pacer = new FramePacer(SixtyFps);
        int accepted = CountAccepted(pacer, sourceRate, seconds: 10);
        Assert.InRange(accepted, 595, 605);
    }

    [Fact]
    public void Frames_closer_together_than_the_interval_allows_are_refused()
    {
        var pacer = new FramePacer(TimeSpan.FromMilliseconds(40));

        Assert.True(pacer.ShouldAccept(TimeSpan.Zero));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(10)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(30)));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(40)));
    }

    [Fact]
    public void A_frame_that_is_almost_due_is_taken_rather_than_waiting_another_refresh()
    {
        var pacer = new FramePacer(TimeSpan.FromMilliseconds(40));

        Assert.True(pacer.ShouldAccept(TimeSpan.Zero));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(36)));
    }

    [Fact]
    public void A_quiet_period_does_not_earn_a_burst_afterwards()
    {
        var pacer = new FramePacer(TimeSpan.FromMilliseconds(40));
        Assert.True(pacer.ShouldAccept(TimeSpan.Zero));

        // Nothing for a second, then frames 5 ms apart: only the first is due.
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1000)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1005)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1010)));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1040)));
    }

    [Fact]
    public void A_zero_interval_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(TimeSpan.Zero));
    }

    private static int CountAccepted(FramePacer pacer, int sourceRate, int seconds)
    {
        int accepted = 0;
        for (int frame = 0; frame < sourceRate * seconds; frame++)
        {
            if (pacer.ShouldAccept(TimeSpan.FromSeconds((double)frame / sourceRate)))
            {
                accepted++;
            }
        }

        return accepted;
    }
}
