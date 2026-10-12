using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Graphics;

namespace ExtendSpace.Experiments.CaptureEncode;

internal enum EncoderPreference
{
    /// <summary>Hardware encoders first, then the software encoder.</summary>
    Auto,
    Hardware,
    Software,
}

internal enum H264Profile
{
    Baseline,
    Main,
    High,
}

/// <summary>What was asked for. None of these are measurements.</summary>
internal sealed record CaptureOptions
{
    /// <summary>Index into the monitor list printed by <c>list</c>; null selects the primary monitor.</summary>
    public int? MonitorIndex { get; init; }

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Stop after this many frames have been handed to the encoder, if sooner than the duration.</summary>
    public int? MaxFrames { get; init; }

    public int FrameRate { get; init; } = 60;

    public int BitrateBitsPerSecond { get; init; } = 12_000_000;

    public double KeyframeIntervalSeconds { get; init; } = 2;

    public H264Profile Profile { get; init; } = H264Profile.High;

    public EncoderPreference Encoder { get; init; } = EncoderPreference.Auto;

    /// <summary>
    /// Size of the encoded frames. Null encodes at the monitor's own size. Otherwise the picture is
    /// scaled to fit inside this size, keeping its shape.
    /// </summary>
    public (int Width, int Height)? OutputSize { get; init; }

    /// <summary>Null means a timestamped file in the current directory.</summary>
    public string? OutputPath { get; init; }

    public bool CaptureCursor { get; init; } = true;

    public bool RequestBorderless { get; init; }

    /// <summary>
    /// Ask Windows for a 1 ms timer for this process while capturing, as media applications do.
    /// On by default because the hardware encoder's events were measured to be held to the default
    /// 15.6 ms tick without it; see docs/architecture/streaming-experiment-01.md.
    /// </summary>
    public bool RaiseTimerResolution { get; init; } = true;

    public int KeyframeIntervalFrames => Math.Max(1, (int)Math.Round(FrameRate * KeyframeIntervalSeconds));

    public TimeSpan FrameInterval => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FrameRate);
}

internal enum CommandKind
{
    Capture,
    List,
    Help,
}

internal sealed record ParsedCommand(CommandKind Kind, CaptureOptions Options);

internal sealed class UsageException(string message) : Exception(message);

internal static class CommandLine
{
    public const string HelpText = """
        ExtendSpace capture -> H.264 experiment

        Captures a real Windows display with Windows.Graphics.Capture, encodes it with a Media
        Foundation H.264 encoder and writes a raw Annex B elementary stream (.h264).

        Usage:
          ExtendSpace.CaptureEncode [capture] [options]
          ExtendSpace.CaptureEncode list
          ExtendSpace.CaptureEncode --help

        Commands:
          capture             Run the experiment (default).
          list                Print the monitors, the graphics adapter and the H.264 encoders found.

        Options:
          --monitor <index>         Monitor to capture, as numbered by 'list'. Default: the primary monitor.
          --duration <seconds>      Capture duration, 0.5 to 600. Default: 10.
          --max-frames <count>      Also stop once this many frames have been submitted to the encoder.
          --fps <rate>              Target frame rate, 1 to 240. Default: 60.
          --output-size <WxH>       Encode at this size instead of the monitor's own, for example 1920x1200.
                                    Both numbers even, 128 to 4096. The picture is scaled on the GPU to
                                    fit, keeping its shape; any space left over is black.
          --bitrate <bits>          Target bitrate in bit/s; 'k' and 'M' suffixes allowed. Default: 12M.
          --gop-seconds <seconds>   Keyframe interval, 0.1 to 60. Default: 2.
          --profile <name>          baseline, main or high. Default: high.
          --encoder <choice>        auto, hardware or software. Default: auto (hardware, then software).
          --output <path>           Output file; must end in .h264.
                                    Default: .\extendspace-capture-<UTC timestamp>.h264
          --no-cursor               Leave the mouse cursor out of the capture.
          --no-border               Ask Windows for capture without the on-screen capture border.
          --default-timer           Leave the Windows timer resolution alone instead of requesting 1 ms
                                    for this process. For comparison; expect a slower hardware encoder.

        Ctrl+C or Ctrl+Break stops the capture early; the stream is still drained and the file finalised.
        Exit codes: 0 success, 1 the run failed, 2 invalid arguments.
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        CommandKind kind = CommandKind.Capture;
        var options = new CaptureOptions();
        int index = 0;

        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            kind = args[0].ToLowerInvariant() switch
            {
                "capture" => CommandKind.Capture,
                "list" => CommandKind.List,
                "help" => CommandKind.Help,
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
            index = 1;
        }

        while (index < args.Count)
        {
            string name = args[index++];
            string Value() => index < args.Count
                ? args[index++]
                : throw new UsageException($"Option '{name}' needs a value.");

            switch (name)
            {
                case "-h" or "--help" or "-?" or "/?":
                    return new ParsedCommand(CommandKind.Help, options);
                case "--monitor":
                    options = options with { MonitorIndex = ParseInt(name, Value(), 0, 63) };
                    break;
                case "--duration":
                    options = options with { Duration = TimeSpan.FromSeconds(ParseDouble(name, Value(), 0.5, 600)) };
                    break;
                case "--max-frames":
                    options = options with { MaxFrames = ParseInt(name, Value(), 1, 1_000_000) };
                    break;
                case "--fps":
                    options = options with { FrameRate = ParseInt(name, Value(), 1, 240) };
                    break;
                case "--output-size":
                    options = options with { OutputSize = ParseSize(name, Value()) };
                    break;
                case "--bitrate":
                    options = options with { BitrateBitsPerSecond = ParseBitrate(name, Value()) };
                    break;
                case "--gop-seconds":
                    options = options with { KeyframeIntervalSeconds = ParseDouble(name, Value(), 0.1, 60) };
                    break;
                case "--profile":
                    options = options with { Profile = ParseEnum<H264Profile>(name, Value()) };
                    break;
                case "--encoder":
                    options = options with { Encoder = ParseEnum<EncoderPreference>(name, Value()) };
                    break;
                case "--output":
                    options = options with { OutputPath = ParseOutputPath(Value()) };
                    break;
                case "--no-cursor":
                    options = options with { CaptureCursor = false };
                    break;
                case "--no-border":
                    options = options with { RequestBorderless = true };
                    break;
                case "--default-timer":
                    options = options with { RaiseTimerResolution = false };
                    break;
                default:
                    throw new UsageException($"Unknown option '{name}'.");
            }
        }

        return new ParsedCommand(kind, options);
    }

    /// <summary>Accepts plain bit/s or a decimal with a k (thousand) or M (million) suffix.</summary>
    internal static int ParseBitrate(string name, string text)
    {
        double scale = 1;
        string number = text;
        if (text.EndsWith('k') || text.EndsWith('K'))
        {
            scale = 1_000;
            number = text[..^1];
        }
        else if (text.EndsWith('m') || text.EndsWith('M'))
        {
            scale = 1_000_000;
            number = text[..^1];
        }

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new UsageException($"Option '{name}': '{text}' is not a bitrate. Use for example 8000000, 8000k or 8M.");
        }

        double bits = value * scale;
        const int Minimum = 100_000;
        const int Maximum = 200_000_000;
        if (!(bits >= Minimum && bits <= Maximum))
        {
            throw new UsageException($"Option '{name}': the bitrate must be between {Minimum} and {Maximum} bit/s.");
        }

        return (int)Math.Round(bits);
    }

    /// <summary>Accepts WIDTHxHEIGHT, for example 1920x1200.</summary>
    internal static (int Width, int Height) ParseSize(string name, string text)
    {
        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            throw new UsageException($"Option '{name}': '{text}' is not a size. Use WIDTHxHEIGHT, for example 1920x1200.");
        }

        if (!OutputGeometry.IsValidRequest(width, height))
        {
            throw new UsageException(
                $"Option '{name}': width and height must both be even and from {OutputGeometry.MinimumOutputDimension} to {OutputGeometry.MaximumOutputDimension}; got {width} x {height}.");
        }

        return (width, height);
    }

    private static string ParseOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UsageException("Option '--output' needs a file path.");
        }

        if (!path.EndsWith(".h264", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("Option '--output': the file name must end in .h264, because the file is a raw H.264 elementary stream.");
        }

        return path;
    }

    private static int ParseInt(string name, string text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value < minimum
            || value > maximum)
        {
            throw new UsageException($"Option '{name}' must be a whole number from {minimum} to {maximum}; got '{text}'.");
        }

        return value;
    }

    private static double ParseDouble(string name, string text, double minimum, double maximum)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !(value >= minimum && value <= maximum))
        {
            throw new UsageException(string.Create(
                CultureInfo.InvariantCulture,
                $"Option '{name}' must be a number from {minimum} to {maximum}; got '{text}'."));
        }

        return value;
    }

    private static T ParseEnum<T>(string name, string text)
        where T : struct, Enum
    {
        // Numeric strings would otherwise parse as undefined enum values.
        if (int.TryParse(text, out _) || !Enum.TryParse(text, ignoreCase: true, out T value))
        {
            string allowed = string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
            throw new UsageException($"Option '{name}' must be one of: {allowed}; got '{text}'.");
        }

        return value;
    }
}
