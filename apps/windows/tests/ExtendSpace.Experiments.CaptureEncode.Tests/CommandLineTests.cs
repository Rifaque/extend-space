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
    [InlineData("stream", nameof(CommandKind.Stream))]
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
    public void Stream_takes_the_capture_options_and_its_own()
    {
        ParsedCommand command = CommandLine.Parse(
        [
            "stream",
            "--output-size", "1280x720",
            "--fps", "30",
            "--bitrate", "6M",
            "--duration", "120",
            "--port", "30000",
            "--device", "852ec620",
            "--adb", @"C:\sdk\platform-toolsdb.exe",
            "--no-adb-forward",
            "--reconnect-seconds", "5",
        ]);

        Assert.Equal(CommandKind.Stream, command.Kind);
        Assert.Equal((1280, 720), command.Options.OutputSize);
        Assert.Equal(30, command.Options.FrameRate);
        Assert.Equal(TimeSpan.FromSeconds(120), command.Options.Duration);
        Assert.Equal(30000, command.Stream.Port);
        Assert.Equal("852ec620", command.Stream.DeviceSerial);
        Assert.Equal(@"C:\sdk\platform-toolsdb.exe", command.Stream.AdbPath);
        Assert.False(command.Stream.ManageAdbForward);
        Assert.Equal(TimeSpan.FromSeconds(5), command.Stream.ReconnectBudget);
    }

    [Fact]
    public void Stream_defaults_to_the_documented_port_and_sets_the_forward_up_itself()
    {
        StreamOptions stream = CommandLine.Parse(["stream"]).Stream;

        Assert.Equal(27183, stream.Port);
        Assert.Null(stream.DeviceSerial);
        Assert.Null(stream.AdbPath);
        Assert.True(stream.ManageAdbForward);
        Assert.Equal(TimeSpan.FromSeconds(30), stream.ReconnectBudget);
    }

    [Theory]
    [InlineData("--port", "80")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "http")]
    [InlineData("--reconnect-seconds", "0")]
    [InlineData("--device", "")]
    [InlineData("--device", "abc; rm -rf")]
    [InlineData("--device", "two words")]
    [InlineData("--device", "quote\"d")]
    [InlineData("--output", "clip.h264")]
    [InlineData("--max-frames", "10")]
    public void Invalid_stream_arguments_are_usage_errors(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["stream", option, value]));
    }

    [Theory]
    [InlineData("--port", "30000")]
    [InlineData("--device", "852ec620")]
    [InlineData("--reconnect-seconds", "5")]
    public void Stream_options_are_refused_by_the_other_commands(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["capture", option, value]));
        Assert.Throws<UsageException>(() => CommandLine.Parse([option, value]));
    }

    [Fact]
    public void An_emulator_style_serial_is_accepted()
    {
        Assert.Equal("emulator-5554", CommandLine.Parse(["stream", "--device", "emulator-5554"]).Stream.DeviceSerial);
        Assert.Equal("192.168.1.20:5555", CommandLine.Parse(["stream", "--device", "192.168.1.20:5555"]).Stream.DeviceSerial);
    }

    [Fact]
    public void The_keyframe_interval_is_never_shorter_than_one_frame()
    {
        CaptureOptions options = CommandLine.Parse(["--fps", "1", "--gop-seconds", "0.1"]).Options;
        Assert.Equal(1, options.KeyframeIntervalFrames);
    }

    [Fact]
    public void Probe_takes_the_capture_options_and_encodes_unless_told_not_to()
    {
        ParsedCommand command = CommandLine.Parse(["probe", "--monitor", "1", "--output-size", "1920x1080", "--fps", "60", "--duration", "45"]);

        Assert.Equal(CommandKind.Probe, command.Kind);
        Assert.Equal(1, command.Options.MonitorIndex);
        Assert.Equal((1920, 1080), command.Options.OutputSize);
        Assert.Equal(TimeSpan.FromSeconds(45), command.Options.Duration);
        Assert.True(command.ProbeEncodes);
        Assert.False(CommandLine.Parse(["probe", "--no-encode"]).ProbeEncodes);
    }

    [Theory]
    [InlineData("--output", "clip.h264")]
    [InlineData("--max-frames", "10")]
    [InlineData("--port", "30000")]
    [InlineData("--size", "1280x720")]
    [InlineData("--duration", "601")]
    public void Probe_keeps_nothing_and_sends_nothing_so_it_refuses_the_options_that_would(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["probe", option, value]));
    }

    [Fact]
    public void Pattern_defaults_to_a_1080p_flip_model_window_on_every_refresh_until_closed()
    {
        ParsedCommand command = CommandLine.Parse(["pattern"]);

        Assert.Equal(CommandKind.Pattern, command.Kind);
        Assert.Equal((1920, 1080), command.Pattern.Size);
        Assert.False(command.Pattern.FullScreen);
        Assert.Equal(PresentModel.Flip, command.Pattern.Model);
        Assert.Equal(1, command.Pattern.SyncInterval);
        Assert.True(command.Pattern.Topmost);
        Assert.Null(command.PatternDuration);
        Assert.Null(command.Options.MonitorIndex);
    }

    [Fact]
    public void Pattern_reads_its_options_and_may_outlast_a_capture()
    {
        ParsedCommand command = CommandLine.Parse(
            ["pattern", "--monitor", "1", "--duration", "700", "--size", "1280x720", "--fullscreen", "--model", "blt", "--sync-interval", "2", "--no-topmost"]);

        Assert.Equal(1, command.Options.MonitorIndex);
        Assert.Equal(TimeSpan.FromSeconds(700), command.PatternDuration);
        Assert.Equal((1280, 720), command.Pattern.Size);
        Assert.True(command.Pattern.FullScreen);
        Assert.Equal(PresentModel.Blt, command.Pattern.Model);
        Assert.Equal(2, command.Pattern.SyncInterval);
        Assert.False(command.Pattern.Topmost);
    }

    [Theory]
    [InlineData("--fps", "60")]
    [InlineData("--bitrate", "6M")]
    [InlineData("--output", "clip.h264")]
    [InlineData("--port", "30000")]
    [InlineData("--model", "overlay")]
    [InlineData("--sync-interval", "0")]
    [InlineData("--sync-interval", "5")]
    [InlineData("--size", "1921x1080")]
    [InlineData("--duration", "3601")]
    public void Pattern_refuses_capture_options_and_bad_values(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["pattern", option, value]));
    }

    [Theory]
    [InlineData("--size", "1280x720")]
    [InlineData("--model", "blt")]
    [InlineData("--sync-interval", "2")]
    public void Pattern_options_are_refused_by_the_other_commands(string option, string value)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["capture", option, value]));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["stream", option, value]));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["stream", "--fullscreen"]));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["capture", "--no-encode"]));
    }

    [Fact]
    public void A_capture_or_a_stream_is_still_limited_to_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), CommandLine.Parse(["stream", "--duration", "600"]).Options.Duration);
        UsageException refused = Assert.Throws<UsageException>(() => CommandLine.Parse(["stream", "--duration", "700"]));
        Assert.Contains("from 0.5 to 600", refused.Message);
    }

    [Fact]
    public void The_help_text_documents_every_option_the_parser_accepts()
    {
        string[] options =
        [
            "--monitor", "--duration", "--max-frames", "--fps", "--output-size", "--bitrate", "--gop-seconds",
            "--profile", "--encoder", "--output", "--no-cursor", "--no-border", "--default-timer",
            "--port", "--device", "--adb", "--no-adb-forward", "--reconnect-seconds",
            "--no-encode", "--size", "--fullscreen", "--model", "--sync-interval", "--no-topmost",
        ];

        Assert.All(options, option => Assert.Contains(option, CommandLine.HelpText));
    }
}
