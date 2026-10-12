using ExtendSpace.Experiments.CaptureEncode.Graphics;

namespace ExtendSpace.Experiments.CaptureEncode.Tests;

/// <summary>
/// Covers the runner's pure decisions only. Capturing and encoding need a real display and a real
/// encoder and are verified by running the experiment, not here.
/// </summary>
public class ExperimentRunnerTests
{
    private static readonly DisplayMonitor Secondary = new(0, 100, @"\\.\DISPLAY2", IsPrimary: false, -1920, 0, 1920, 1080);
    private static readonly DisplayMonitor Primary = new(1, 200, @"\\.\DISPLAY1", IsPrimary: true, 0, 0, 2560, 1600);

    [Fact]
    public void The_primary_monitor_is_chosen_when_none_is_requested()
    {
        Assert.Same(Primary, ExperimentRunner.SelectMonitor([Secondary, Primary], requestedIndex: null));
    }

    [Fact]
    public void The_first_monitor_is_chosen_if_windows_marks_none_as_primary()
    {
        Assert.Same(Secondary, ExperimentRunner.SelectMonitor([Secondary], requestedIndex: null));
    }

    [Fact]
    public void A_requested_monitor_is_chosen_by_index()
    {
        Assert.Same(Secondary, ExperimentRunner.SelectMonitor([Secondary, Primary], requestedIndex: 0));
        Assert.Same(Primary, ExperimentRunner.SelectMonitor([Secondary, Primary], requestedIndex: 1));
    }

    [Fact]
    public void A_monitor_index_that_does_not_exist_is_an_error_that_says_how_many_there_are()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ExperimentRunner.SelectMonitor([Secondary, Primary], requestedIndex: 2));

        Assert.Contains("2 monitor(s)", exception.Message);
    }

    [Fact]
    public void No_monitors_at_all_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => ExperimentRunner.SelectMonitor([], requestedIndex: null));
    }

    [Fact]
    public void The_default_output_file_is_a_timestamped_h264_file()
    {
        string name = ExperimentRunner.DefaultOutputPath(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        Assert.Equal("extendspace-capture-20260304-050607Z.h264", name);
    }
}
