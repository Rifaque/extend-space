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
    public void A_quiet_period_earns_one_extra_frame_afterwards_and_no_more()
    {
        var pacer = new FramePacer(TimeSpan.FromMilliseconds(40));
        Assert.True(pacer.ShouldAccept(TimeSpan.Zero));

        // Nothing for a second, then frames 5 ms apart: the one that is due, one spare, then the usual spacing.
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1000)));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1005)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1010)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1015)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1030)));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(1040)));

        // However long the pause, the allowance does not grow.
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(60_000)));
        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(60_001)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(60_002)));
    }

    [Theory]
    [InlineData(new[] { 14, 21 })]
    [InlineData(new[] { 14, 14, 21 })]
    [InlineData(new[] { 21, 14, 14, 21, 14 })]
    [InlineData(new[] { 12, 18, 18 })]
    public void A_source_near_the_target_rate_that_lands_on_display_refreshes_loses_almost_nothing(int[] spacingMilliseconds)
    {
        // 60 fps content shown on a faster display reaches capture early and late by turns. The
        // source is at or below the target rate, so nearly every frame should go through.
        var pacer = new FramePacer(SixtyFps);
        var time = TimeSpan.Zero;
        int offered = 0;
        int accepted = 0;

        for (int i = 0; i < 600; i++)
        {
            offered++;
            if (pacer.ShouldAccept(time))
            {
                accepted++;
            }

            time += TimeSpan.FromMilliseconds(spacingMilliseconds[i % spacingMilliseconds.Length]);
        }

        double offeredRate = offered / time.TotalSeconds;
        double acceptedRate = accepted / time.TotalSeconds;
        Assert.True(acceptedRate <= 60.5, $"accepted {acceptedRate:0.0} fps");
        Assert.True(acceptedRate >= Math.Min(offeredRate, 60) - 1.0, $"offered {offeredRate:0.0} fps, accepted only {acceptedRate:0.0} fps");
    }

    [Fact]
    public void Timestamps_that_do_not_advance_earn_nothing()
    {
        var pacer = new FramePacer(TimeSpan.FromMilliseconds(40));

        Assert.True(pacer.ShouldAccept(TimeSpan.FromMilliseconds(100)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(100)));
        Assert.False(pacer.ShouldAccept(TimeSpan.FromMilliseconds(90)));
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
