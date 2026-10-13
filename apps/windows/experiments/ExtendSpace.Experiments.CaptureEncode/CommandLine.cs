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

/// <summary>What the stream command needs beyond the capture options.</summary>
internal sealed record StreamOptions
{
    /// <summary>The loopback TCP port adb forwards to the Android app.</summary>
    public int Port { get; init; } = Streaming.Protocol.DefaultHostPort;

    /// <summary>The adb serial of the device, for when more than one is attached.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Where adb.exe is; null finds it through ANDROID_HOME or PATH.</summary>
    public string? AdbPath { get; init; }

    /// <summary>False leaves the port forward to the user (--no-adb-forward).</summary>
    public bool ManageAdbForward { get; init; } = true;

    /// <summary>How long to keep trying to open, or re-open, the connection before giving up.</summary>
    public TimeSpan ReconnectBudget { get; init; } = Streaming.ReconnectBackoff.DefaultBudget;
}

/// <summary>How the test pattern's window hands its pictures to Windows.</summary>
internal enum PresentModel
{
    /// <summary>A flip-model swap chain, as games and video players use. Windows may show it without composing it.</summary>
    Flip,

    /// <summary>A blt-model swap chain: every picture is copied through the desktop compositor.</summary>
    Blt,
}

/// <summary>What the pattern command needs. It shares --monitor and --duration with the others.</summary>
internal sealed record PatternOptions
{
    /// <summary>The window's size in pixels; it is centred on the monitor.</summary>
    public (int Width, int Height) Size { get; init; } = (1920, 1080);

    /// <summary>Cover the whole monitor instead of using <see cref="Size"/>.</summary>
    public bool FullScreen { get; init; }

    public PresentModel Model { get; init; } = PresentModel.Flip;

    /// <summary>Present on every n-th display refresh.</summary>
    public int SyncInterval { get; init; } = 1;

    /// <summary>Keep the window above others, so that it cannot be covered and stop being a source.</summary>
    public bool Topmost { get; init; } = true;
}

internal enum CommandKind
{
    Capture,
    Stream,
    Probe,
    Pattern,
    List,
    Help,
}

internal sealed record ParsedCommand(CommandKind Kind, CaptureOptions Options)
{
    public StreamOptions Stream { get; init; } = new();

    public PatternOptions Pattern { get; init; } = new();

    /// <summary>For 'probe': false counts frames without encoding them (--no-encode).</summary>
    public bool ProbeEncodes { get; init; } = true;

    /// <summary>For 'pattern': null runs until the window is closed.</summary>
    public TimeSpan? PatternDuration { get; init; }
}

internal sealed class UsageException(string message) : Exception(message);

internal static class CommandLine
{
    public const string HelpText = """
        ExtendSpace capture -> H.264 experiment

        Captures a real Windows display with Windows.Graphics.Capture, encodes it with a Media
        Foundation H.264 encoder and writes a raw Annex B elementary stream (.h264).

        Usage:
          ExtendSpace.CaptureEncode [capture] [options]
          ExtendSpace.CaptureEncode stream [options]
          ExtendSpace.CaptureEncode probe [options]
          ExtendSpace.CaptureEncode pattern [options]
          ExtendSpace.CaptureEncode list
          ExtendSpace.CaptureEncode --help

        Commands:
          capture             Capture to a .h264 file (default).
          stream              Capture and send live to the ExtendSpace Android app over USB, through
                              an adb port forward. Open the app's live screen on the device first.
          probe               Capture and encode as 'stream' does, but send nothing and keep nothing:
                              print each second what the capture API delivered. For finding out
                              whether Windows has stopped delivering frames.
          pattern             Show an animated test pattern that presents a new picture on every
                              display refresh, with a millisecond clock and a frame counter. A
                              controlled source for the other commands; it captures nothing.
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

        Options for 'stream' only (it takes the options above too, except --output and --max-frames;
        --duration counts from the moment the stream starts):
          --port <number>           Loopback TCP port to forward and connect to. Default: 27183.
          --device <serial>         adb serial of the device, when more than one is attached.
          --adb <path>              adb.exe to use. Default: found through ANDROID_HOME, then PATH.
          --no-adb-forward          Do not run 'adb forward'; the port is already forwarded.
          --reconnect-seconds <n>   Give up if the connection cannot be opened, or re-opened after a
                                    loss, within this many seconds, 1 to 600. Default: 30.

        Options for 'probe' only (it takes the capture options too, except --output and --max-frames):
          --no-encode               Count the frames and release them without encoding.

        Options for 'pattern' only (it also takes --monitor, and --duration up to 3600; without
        --duration it runs until closed with Esc, a right-click on it, or Ctrl+C):
          --size <WxH>              Window size in pixels, centred on the monitor. Default: 1920x1080.
          --fullscreen              Cover the whole monitor instead.
          --model <flip|blt>        How the window presents. flip (the default) is what games and
                                    video players use, and Windows may put it on screen without
                                    composing it. blt is always copied through the compositor.
          --sync-interval <n>       Present on every n-th display refresh, 1 to 4. Default: 1.
          --no-topmost              Let other windows cover the pattern.

        Ctrl+C or Ctrl+Break stops the capture early; the stream is still drained and the file finalised.
        Exit codes: 0 success, 1 the run failed, 2 invalid arguments.
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        CommandKind kind = CommandKind.Capture;
        var options = new CaptureOptions();
        var stream = new StreamOptions();
        var pattern = new PatternOptions();
        var given = new List<string>();
        bool probeEncodes = true;
        string durationText = string.Empty;
        int index = 0;

        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            kind = args[0].ToLowerInvariant() switch
            {
                "capture" => CommandKind.Capture,
                "stream" => CommandKind.Stream,
                "probe" => CommandKind.Probe,
                "pattern" => CommandKind.Pattern,
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

            given.Add(name);
            switch (name)
            {
                case "-h" or "--help" or "-?" or "/?":
                    return new ParsedCommand(CommandKind.Help, options);
                case "--monitor":
                    options = options with { MonitorIndex = ParseInt(name, Value(), 0, 63) };
                    break;
                case "--duration":
                    // The pattern may run longer than anything is captured for: it has to be there
                    // before a capture starts and after it ends.
                    durationText = Value();
                    options = options with { Duration = TimeSpan.FromSeconds(ParseDouble(name, durationText, 0.5, MaximumPatternSeconds)) };
                    break;
                case "--no-encode":
                    probeEncodes = false;
                    break;
                case "--size":
                    pattern = pattern with { Size = ParseSize(name, Value()) };
                    break;
                case "--fullscreen":
                    pattern = pattern with { FullScreen = true };
                    break;
                case "--model":
                    pattern = pattern with { Model = ParseEnum<PresentModel>(name, Value()) };
                    break;
                case "--sync-interval":
                    pattern = pattern with { SyncInterval = ParseInt(name, Value(), 1, 4) };
                    break;
                case "--no-topmost":
                    pattern = pattern with { Topmost = false };
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
                case "--port":
                    stream = stream with { Port = ParseInt(name, Value(), 1024, 65535) };
                    break;
                case "--device":
                    stream = stream with { DeviceSerial = ParseSerial(name, Value()) };
                    break;
                case "--adb":
                    stream = stream with { AdbPath = Value() };
                    break;
                case "--no-adb-forward":
                    stream = stream with { ManageAdbForward = false };
                    break;
                case "--reconnect-seconds":
                    stream = stream with { ReconnectBudget = TimeSpan.FromSeconds(ParseInt(name, Value(), 1, 600)) };
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

        if (kind == CommandKind.Stream && (options.OutputPath is not null || options.MaxFrames is not null))
        {
            throw new UsageException("'stream' sends video to the device; it takes neither --output nor --max-frames.");
        }

        if (kind == CommandKind.Probe && (options.OutputPath is not null || options.MaxFrames is not null))
        {
            throw new UsageException("'probe' keeps nothing; it takes neither --output nor --max-frames.");
        }

        if (kind != CommandKind.Stream && stream != new StreamOptions())
        {
            throw new UsageException("--port, --device, --adb, --no-adb-forward and --reconnect-seconds only apply to 'stream'.");
        }

        if (kind != CommandKind.Probe && !probeEncodes)
        {
            throw new UsageException("--no-encode only applies to 'probe'.");
        }

        if (kind != CommandKind.Pattern && pattern != new PatternOptions())
        {
            throw new UsageException("--size, --fullscreen, --model, --sync-interval and --no-topmost only apply to 'pattern'.");
        }

        if (kind == CommandKind.Pattern && given.FirstOrDefault(option => !PatternOptionNames.Contains(option)) is { } foreign)
        {
            throw new UsageException($"'pattern' shows a picture and captures nothing; it does not take '{foreign}'.");
        }

        if (kind != CommandKind.Pattern && options.Duration > TimeSpan.FromSeconds(MaximumCaptureSeconds))
        {
            throw new UsageException(string.Create(
                CultureInfo.InvariantCulture,
                $"Option '--duration' must be a number from 0.5 to {MaximumCaptureSeconds}; got '{durationText}'."));
        }

        return new ParsedCommand(kind, options)
        {
            Stream = stream,
            Pattern = pattern,
            ProbeEncodes = probeEncodes,
            PatternDuration = kind == CommandKind.Pattern && given.Contains("--duration") ? options.Duration : null,
        };
    }

    private const int MaximumCaptureSeconds = 600;
    private const int MaximumPatternSeconds = 3600;

    private static readonly string[] PatternOptionNames =
        ["--monitor", "--duration", "--size", "--fullscreen", "--model", "--sync-interval", "--no-topmost"];

    /// <summary>An adb serial goes onto a command line, so it is held to the characters serials are made of.</summary>
    private static string ParseSerial(string name, string text)
    {
        if (text.Length is 0 or > 128 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_'))
        {
            throw new UsageException($"Option '{name}': '{text}' does not look like an adb device serial.");
        }

        return text;
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
