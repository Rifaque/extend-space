using System.Diagnostics;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>
/// Sets up and removes the adb port forward that is the whole of the USB transport: a TCP port on
/// this PC's loopback interface, owned by adb, carried over the cable to a local (non-network)
/// socket in the Android app. Nothing listens on a network interface on either device.
/// </summary>
internal sealed class AdbForward : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Shorter than the session waits for its own thread when it is stopped.</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(4);

    private readonly string _adb;
    private readonly string? _serial;
    private readonly int _port;
    private bool _active;

    private AdbForward(string adb, string? serial, int port)
    {
        _adb = adb;
        _serial = serial;
        _port = port;
    }

    /// <summary>The command line that was run, for the log and for reproducing it by hand.</summary>
    public string Description => $"adb {Arguments(ForwardCommand)}";

    private string ForwardCommand => $"forward tcp:{_port} localabstract:{Protocol.LocalSocketName}";

    /// <summary>
    /// Runs <c>adb forward</c> again. adb drops a forward when its device disconnects, and does not
    /// bring it back when the device returns, so a session that wants to reconnect has to ask again.
    /// Asking when the forward is still there changes nothing.
    /// </summary>
    /// <returns>Null when the forward is in place; otherwise adb's own account of why not.</returns>
    public string? Restore()
    {
        // If adb's server is not running, this command starts it. The server must outlive the
        // command, so on a timeout only the command itself is ended, not what it started.
        (int exitCode, string output) = Run(ForwardCommand, RestoreTimeout, killTree: false);
        if (exitCode != 0)
        {
            return LastLine(output);
        }

        _active = true;
        return null;
    }

    private static string LastLine(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "adb failed without a message";
    }

    /// <summary>Runs <c>adb forward</c>. Throws with adb's own message if it fails.</summary>
    public static AdbForward Create(string? adbPath, string? serial, int port)
    {
        string adb = adbPath ?? Locate()
            ?? throw new InvalidOperationException(
                "adb was not found. Put it on PATH, set ANDROID_HOME, or pass --adb <path>; or set the forward up yourself and pass --no-adb-forward.");

        var forward = new AdbForward(adb, serial, port);
        (int exitCode, string output) = forward.Run(forward.ForwardCommand, CommandTimeout, killTree: true);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{forward.Description}' failed with exit code {exitCode}: {output}. Is the device connected, authorised and unique (--device <serial>)?");
        }

        forward._active = true;
        return forward;
    }

    /// <summary>Finds adb.exe in the Android SDK the environment points at, or on PATH.</summary>
    internal static string? Locate()
    {
        IEnumerable<string?> sdkRoots =
        [
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
        ];

        IEnumerable<string> candidates = sdkRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root!, "platform-tools", "adb.exe"))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), "adb.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Removes the forward, so that the loopback port stops existing when the session does.</summary>
    public void Dispose()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        Run($"forward --remove tcp:{_port}", CommandTimeout, killTree: true);
    }

    private string Arguments(string command) => _serial is null ? command : $"-s {_serial} {command}";

    private (int ExitCode, string Output) Run(string command, TimeSpan timeout, bool killTree)
    {
        var start = new ProcessStartInfo(_adb, Arguments(command))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{_adb}' could not be started.");

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: killTree);
            return (-1, $"adb did not finish within {timeout.TotalSeconds:0} s");
        }

        return (process.ExitCode, $"{output.Result} {error.Result}".Trim());
    }
}
