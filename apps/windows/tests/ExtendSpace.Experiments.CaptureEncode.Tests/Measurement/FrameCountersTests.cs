using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Measurement;

public class FrameCountersTests
{
    [Fact]
    public void Every_delivered_frame_has_exactly_one_outcome()
    {
        var counters = new FrameCounters
        {
            Delivered = 600,
            SkippedByPacer = 40,
            DroppedEncoderBusy = 7,
            DroppedNoInputSurface = 2,
            DiscardedAtStop = 1,
            Submitted = 550,
            Encoded = 550,
        };

        Assert.Equal(9, counters.Dropped);
        Assert.Equal(0, counters.Unaccounted);
        Assert.Equal(0, counters.LostInEncoder);
    }

    [Fact]
    public void A_frame_lost_part_way_through_shows_up_as_unaccounted()
    {
        var counters = new FrameCounters { Delivered = 10, Submitted = 9, Encoded = 9 };
        Assert.Equal(1, counters.Unaccounted);
    }

    [Fact]
    public void Frames_the_encoder_kept_are_reported()
    {
        var counters = new FrameCounters { Delivered = 10, Submitted = 10, Encoded = 8 };
        Assert.Equal(2, counters.LostInEncoder);
    }
}
