using ExtendSpace.Experiments.CaptureEncode.Pattern;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Pattern;

public class PatternLayoutTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(2560, 1600)]
    [InlineData(640, 360)]
    public void Every_box_lies_inside_the_window(int width, int height)
    {
        var boxes = new List<(PatternColor Color, Box Box)>();
        foreach (long frame in new long[] { 0, 1, 29, 30, 123_456, 999_999 })
        {
            PatternLayout.Build(width, height, frame, frame * 7, boxes);

            Assert.NotEmpty(boxes);
            Assert.All(boxes, item =>
            {
                Assert.InRange(item.Box.Left, 0, width);
                Assert.InRange(item.Box.Right, item.Box.Left, width);
                Assert.InRange(item.Box.Top, 0, height);
                Assert.InRange(item.Box.Bottom, item.Box.Top, height);
            });
        }
    }

    [Fact]
    public void Successive_frames_differ_so_that_every_present_is_a_new_picture()
    {
        var first = new List<(PatternColor Color, Box Box)>();
        var second = new List<(PatternColor Color, Box Box)>();

        // The same instant, one frame apart: the counter, the stepping cell and the mosaic all move.
        PatternLayout.Build(1920, 1080, 41, 5_000, first);
        PatternLayout.Build(1920, 1080, 42, 5_000, second);

        Assert.NotEqual(first, second);
        int mosaicCells = PatternLayout.MosaicColumns * PatternLayout.MosaicRows;
        int changed = first.TakeLast(mosaicCells).Zip(second.TakeLast(mosaicCells)).Count(pair => pair.First.Color != pair.Second.Color);
        Assert.True(changed > mosaicCells / 2, $"only {changed} of {mosaicCells} mosaic cells changed colour");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 480)]
    [InlineData(999, 1918)]
    [InlineData(1000, 0)]
    [InlineData(61_500, 960)]
    public void The_sweep_position_is_the_millisecond_within_the_second(long elapsedMilliseconds, int expectedLeft)
    {
        Assert.Equal(expectedLeft, PatternLayout.SweepLeft(1920, elapsedMilliseconds));
    }

    [Fact]
    public void The_stepping_cell_moves_by_one_per_frame_and_wraps()
    {
        Assert.Equal(0, PatternLayout.TickCell(0));
        Assert.Equal(1, PatternLayout.TickCell(1));
        Assert.Equal(PatternLayout.TickCells - 1, PatternLayout.TickCell(PatternLayout.TickCells - 1));
        Assert.Equal(0, PatternLayout.TickCell(PatternLayout.TickCells));
    }

    [Fact]
    public void Each_digit_lights_a_different_set_of_segments()
    {
        byte[] segments = [.. Enumerable.Range(0, 10).Select(SevenSegment.SegmentsOf)];

        Assert.Equal(10, segments.Distinct().Count());
        Assert.Equal(7, int.PopCount(segments[8]));
        Assert.Equal(2, int.PopCount(segments[1]));
        Assert.Equal(3, int.PopCount(segments[7]));
    }

    [Fact]
    public void A_digit_is_drawn_as_one_bar_per_lit_segment_inside_its_cell()
    {
        for (int digit = 0; digit <= 9; digit++)
        {
            var boxes = new List<(PatternColor Color, Box Box)>();
            SevenSegment.AddDigit(boxes, PatternColor.White, digit, left: 100, top: 50, width: 80, height: 160);

            Assert.Equal(int.PopCount(SevenSegment.SegmentsOf(digit)), boxes.Count);
            Assert.All(boxes, item =>
            {
                Assert.InRange(item.Box.Left, 100, 180);
                Assert.InRange(item.Box.Right, 100, 180);
                Assert.InRange(item.Box.Top, 50, 210);
                Assert.InRange(item.Box.Bottom, 50, 210);
                Assert.True(item.Box.Right > item.Box.Left && item.Box.Bottom > item.Box.Top);
            });
        }
    }
}

public class PresentTimelineTests
{
    private const long TicksPerSecond = 1_000_000;

    [Fact]
    public void A_display_that_gets_a_picture_on_every_refresh_has_no_short_seconds()
    {
        // 143.86 Hz, which Windows reports as 143: 6951 microseconds apart, for five seconds.
        var timeline = new PresentTimeline(expectedPerSecond: 143, TicksPerSecond);
        var seconds = new List<PresentSecond>();
        for (int i = 0; i < 730; i++)
        {
            if (timeline.Add(i * 6951L) is { } second)
            {
                seconds.Add(second);
            }
        }

        Assert.Equal(730, timeline.Presented);
        Assert.Equal(5, seconds.Count);
        Assert.All(seconds, second => Assert.InRange(second.Presented, 143, 144));
        Assert.All(seconds, second => Assert.Equal(0, second.Shortfall));
        Assert.Equal((0, 0), (timeline.SecondsShort, timeline.Shortfall));
    }

    [Fact]
    public void A_present_belongs_to_the_second_it_falls_in()
    {
        var timeline = new PresentTimeline(expectedPerSecond: 100, TicksPerSecond);
        PresentSecond? first = null;
        for (int i = 0; i <= 100 && first is null; i++)
        {
            first = timeline.Add(i * 10_000L);
        }

        // Presents at 0, 10 ms ... 990 ms are the first second: a hundred of them, not a hundred and one.
        Assert.Equal(100, Assert.NotNull(first).Presented);
        Assert.Equal(1.0, first.Value.Seconds);
    }

    [Fact]
    public void A_second_with_fewer_pictures_than_refreshes_is_short_by_the_difference()
    {
        var timeline = new PresentTimeline(expectedPerSecond: 100, TicksPerSecond);
        var seconds = new List<PresentSecond>();
        void Present(long at)
        {
            if (timeline.Add(at) is { } second)
            {
                seconds.Add(second);
            }
        }

        // One full second, then a second in which the pattern stalls for a quarter of a second.
        for (int i = 0; i < 100; i++)
        {
            Present(i * 10_000L);
        }

        for (int i = 0; i < 75; i++)
        {
            Present(1_000_000 + (i * 10_000L));
        }

        Present(2_000_000);

        Assert.Equal([0, 25], seconds.Select(second => second.Shortfall));
        Assert.Equal((1, 25), (timeline.SecondsShort, timeline.Shortfall));
        Assert.Equal(260_000, timeline.LongestIntervalTicks);
    }

    [Fact]
    public void A_slow_start_is_shown_but_not_held_against_the_run()
    {
        var timeline = new PresentTimeline(expectedPerSecond: 100, TicksPerSecond);
        PresentSecond? first = null;

        // Half the first second is spent starting up.
        for (int i = 50; i <= 100; i++)
        {
            first ??= timeline.Add(i * 10_000L);
        }

        // The first second is the one that started at the first present, so it runs to 1.5 s.
        for (int i = 101; i <= 150 && first is null; i++)
        {
            first = timeline.Add(i * 10_000L);
        }

        Assert.Equal(0, Assert.NotNull(first).Shortfall);
        Assert.Equal((0, 0), (timeline.SecondsShort, timeline.Shortfall));
    }

    [Fact]
    public void Without_a_refresh_rate_seconds_are_counted_but_not_judged()
    {
        var timeline = new PresentTimeline(expectedPerSecond: null, TicksPerSecond);
        PresentSecond? first = null;
        for (int i = 0; i <= 10 && first is null; i++)
        {
            first = timeline.Add(i * 100_000L);
        }

        Assert.Equal(10, Assert.NotNull(first).Presented);
        Assert.Null(first.Value.Shortfall);
        Assert.Equal(0, timeline.SecondsShort);
    }

    [Fact]
    public void Presents_that_were_not_shown_are_counted_apart_from_those_that_were()
    {
        var timeline = new PresentTimeline(expectedPerSecond: 60, TicksPerSecond);
        timeline.Add(0);
        timeline.AddOccluded();
        timeline.AddOccluded();

        Assert.Equal(1, timeline.Presented);
        Assert.Equal(2, timeline.Occluded);
    }
}
