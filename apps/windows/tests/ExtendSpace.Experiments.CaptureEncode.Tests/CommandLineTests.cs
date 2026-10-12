namespace ExtendSpace.Experiments.CaptureEncode.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_means_a_ten_second_capture_of_the_primary_monitor_at_60_fps()
    {
        ParsedCommand command = CommandLine.Parse([]);

        Assert.Equal(CommandKind.Capture, command.Kind);
        Assert.Null(command.Options.MonitorIndex);
        Assert.Equal(TimeSpan.FromSeconds(10), command.Options.Duration);
        Assert.Null(command.Options.MaxFrames);
        Assert.Equal(60, command.Options.FrameRate);
        Assert.Equal(12_000_000, command.Options.BitrateBitsPerSecond);
        Assert.Equal(120, command.Options.KeyframeIntervalFrames);
        Assert.Equal(H264Profile.High, command.Options.Profile);
        Assert.Equal(EncoderPreference.Auto, command.Options.Encoder);
        Assert.Null(command.Options.OutputPath);
        Assert.Null(command.Options.OutputSize);
        Assert.True(command.Options.CaptureCursor);
        Assert.False(command.Options.RequestBorderless);
        Assert.True(command.Options.RaiseTimerResolution);
    }

    [Fact]
    public void Every_option_is_read()
    {
        ParsedCommand command = CommandLine.Parse(
        [
            "capture",
            "--monitor", "1",
            "--duration", "2.5",
            "--max-frames", "90",
            "--fps", "30",
            "--bitrate", "6M",
            "--output-size", "1920x1200",
            "--gop-seconds", "0.5",
            "--profile", "main",
            "--encoder", "Software",
            "--output", @"out\clip.H264",
            "--no-cursor",
            "--no-border",
            "--default-timer",
        ]);

        CaptureOptions options = command.Options;
        Assert.Equal(CommandKind.Capture, command.Kind);
        Assert.Equal(1, options.MonitorIndex);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.Duration);
        Assert.Equal(90, options.MaxFrames);
        Assert.Equal(30, options.FrameRate);
        Assert.Equal(6_000_000, options.BitrateBitsPerSecond);
        Assert.Equal((1920, 1200), options.OutputSize);
        Assert.Equal(15, options.KeyframeIntervalFrames);
        Assert.Equal(H264Profile.Main, options.Profile);
        Assert.Equal(EncoderPreference.Software, options.Encoder);
        Assert.Equal(@"out\clip.H264", options.OutputPath);
        Assert.False(options.CaptureCursor);
        Assert.True(options.RequestBorderless);
        Assert.False(options.RaiseTimerResolution);
    }

    [Theory]
    [InlineData("capture", nameof(CommandKind.Capture))]
    [InlineData("list", nameof(CommandKind.List))]
    [InlineData("help", nameof(CommandKind.Help))]
    [InlineData("--help", nameof(CommandKind.Help))]
    [InlineData("-h", nameof(CommandKind.Help))]
    public void Commands_are_recognised(string argument, string expected)
    {
        Assert.Equal(Enum.Parse<CommandKind>(expected), CommandLine.Parse([argument]).Kind);
    }

    [Theory]
    [InlineData("8000000", 8_000_000)]
    [InlineData("8000k", 8_000_000)]
    [InlineData("8M", 8_000_000)]
    [InlineData("2.5m", 2_500_000)]
    [InlineData("100000", 100_000)]
    public void Bitrates_accept_plain_bits_and_k_or_M_suffixes(string text, int expected)
    {
        Assert.Equal(expected, CommandLine.ParseBitrate("--bitrate", text));
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("99999")]
    [InlineData("201M")]
    [InlineData("-4M")]
    [InlineData("NaN")]
    public void Bitrates_outside_the_supported_range_or_not_numbers_are_rejected(string text)
    {
        Assert.Throws<UsageException>(() => CommandLine.ParseBitrate("--bitrate", text));
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "241")]
    [InlineData("--fps", "sixty")]
    [InlineData("--fps", "59.94")]
    [InlineData("--duration", "0")]
    [InlineData("--duration", "601")]
    [InlineData("--duration", "NaN")]
    [InlineData("--monitor", "-1")]
    [InlineData("--max-frames", "0")]
    [InlineData("--gop-seconds", "0")]
    [InlineData("--profile", "ultra")]
    [InlineData("--profile", "2")]
    [InlineData("--encoder", "gpu")]
    [InlineData("--output-size", "1920")]
    [InlineData("--output-size", "1920x")]
    [InlineData("--output-size", "1920x1200x60")]
    [InlineData("--output-size", "widexhigh")]
    [InlineData("--output-size", "1921x1200")]
    [InlineData("--output-size", "64x64")]
    [InlineData("--output-size", "8192x4320")]
    [InlineData("--output-size", "-1920x1200")]
    [InlineData("--output", "clip.mp4")]
    [InlineData("--output", " ")]
    public void Invalid_values_are_usage_errors(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse([option, value]));
    }

    [Theory]
    [InlineData("1920x1200", 1920, 1200)]
    [InlineData("1280X720", 1280, 720)]
    [InlineData("128x4096", 128, 4096)]
    public void Output_sizes_are_width_x_height(string text, int width, int height)
    {
        Assert.Equal((width, height), CommandLine.ParseSize("--output-size", text));
    }

    [Fact]
    public void An_option_without_its_value_is_a_usage_error()
    {
        UsageException exception = Assert.Throws<UsageException>(() => CommandLine.Parse(["--fps"]));
        Assert.Contains("--fps", exception.Message);
    }

    [Theory]
    [InlineData("--frames")]
    [InlineData("record")]
    public void Unknown_options_and_commands_are_usage_errors(string argument)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse([argument]));
    }

    [Fact]
    public void The_keyframe_interval_is_never_shorter_than_one_frame()
    {
        CaptureOptions options = CommandLine.Parse(["--fps", "1", "--gop-seconds", "0.1"]).Options;
        Assert.Equal(1, options.KeyframeIntervalFrames);
    }

    [Fact]
    public void The_help_text_documents_every_option_the_parser_accepts()
    {
        string[] options =
        [
            "--monitor", "--duration", "--max-frames", "--fps", "--output-size", "--bitrate", "--gop-seconds",
            "--profile", "--encoder", "--output", "--no-cursor", "--no-border", "--default-timer",
        ];

        Assert.All(options, option => Assert.Contains(option, CommandLine.HelpText));
    }
}
