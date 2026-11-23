using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ExtendSpace.Core.Input;

namespace ExtendSpace.Linux.X11;

/// <summary>The device names a Linux session asks for (D98, D99).</summary>
public static class LinuxSourceNames
{
    /// <summary>Extend: the virtual display ExtendSpace creates for the session (portal or RandR).</summary>
    public const string Virtual = "extendspace-virtual";

    /// <summary>Mirror on Wayland: the monitor the user picks in the desktop's sharing dialog.</summary>
    public const string PortalMonitor = "portal-monitor";
}

/// <summary>Runs a program and returns its exit code and output. Replaced in tests.</summary>
public interface ICommandRunner
{
    (int ExitCode, string Output) Run(string program, params string[] arguments);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public (int ExitCode, string Output) Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"{program} did not finish in 10 s");
            }

            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, $"{program} could not be run: {e.Message}");
        }
    }
}

/// <summary>The X screen's framebuffer sizes as <c>xrandr</c> prints them.</summary>
public sealed record X11Screen(int Width, int Height, int MaxWidth, int MaxHeight);

/// <summary>
/// Extend on an X11 session (D99, experimental): the framebuffer is enlarged to the right of everything
/// and a RandR 1.5 monitor named <see cref="MonitorName"/> is declared over the new area, with no output
/// behind it. Nothing scans it out; ExtendSpace captures it. Window managers that honour RandR monitors
/// treat it as a display; others may not (unverified, ES-152). Physical outputs keep their modes and
/// positions. On release the monitor is deleted and the framebuffer restored to its old size.
/// </summary>
public sealed class X11VirtualDisplay(ICommandRunner runner, string? display = null)
{
    public const string MonitorName = "EXTENDSPACE-VIRTUAL";
    private static readonly Regex ScreenLine = new(@"current (\d+) x (\d+), maximum (\d+) x (\d+)", RegexOptions.CultureInvariant);
    private readonly string _display = display ?? Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
    private X11Screen? _before;

    public static bool IsOurs(string monitorName) => string.Equals(monitorName, MonitorName, StringComparison.Ordinal);

    public static X11Screen? ParseScreen(string xrandrOutput)
    {
        Match m = ScreenLine.Match(xrandrOutput);
        return m.Success
            ? new X11Screen(Int(m.Groups[1]), Int(m.Groups[2]), Int(m.Groups[3]), Int(m.Groups[4]))
            : null;

        static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Where the virtual monitor goes and the framebuffer it needs; or why it cannot.</summary>
    public static (ScreenRect Region, int FramebufferWidth, int FramebufferHeight)? Plan(X11Screen screen, int width, int height, out string? problem)
    {
        int fbWidth = screen.Width + width;
        int fbHeight = Math.Max(screen.Height, height);
        if (fbWidth > screen.MaxWidth || fbHeight > screen.MaxHeight)
        {
            problem = $"The X server's screen can be at most {screen.MaxWidth} × {screen.MaxHeight}; a {width} × {height} virtual display beside {screen.Width} × {screen.Height} does not fit.";
            return null;
        }

        problem = null;
        return (new ScreenRect(screen.Width, 0, width, height), fbWidth, fbHeight);
    }

    /// <summary>Creates the virtual monitor; returns its rectangle or throws with what went wrong.</summary>
    public ScreenRect Create(int width, int height)
    {
        (int code, string output) = runner.Run("xrandr", "--display", _display);
        X11Screen screen = code == 0 && ParseScreen(output) is { } parsed
            ? parsed
            : throw new InvalidOperationException($"xrandr could not read the screen: {output.Trim()}");
        if (Plan(screen, width, height, out string? problem) is not { } plan)
        {
            throw new InvalidOperationException(problem);
        }

        _before = screen;
        Run("--fb", $"{plan.FramebufferWidth}x{plan.FramebufferHeight}");
        // Physical size in millimetres for 96 dpi, so toolkits scale it like an ordinary monitor.
        int mmWidth = (int)Math.Round(width * 25.4 / 96);
        int mmHeight = (int)Math.Round(height * 25.4 / 96);
        try
        {
            Run("--setmonitor", MonitorName, string.Create(CultureInfo.InvariantCulture, $"{width}/{mmWidth}x{height}/{mmHeight}+{plan.Region.Left}+{plan.Region.Top}"), "none");
        }
        catch
        {
            Remove();
            throw;
        }

        return plan.Region;
    }

    /// <summary>Deletes the monitor and restores the framebuffer; safe to call when nothing was created.</summary>
    public void Remove()
    {
        if (_before is not { } before)
        {
            return;
        }

        _before = null;
        runner.Run("xrandr", "--display", _display, "--delmonitor", MonitorName);
        runner.Run("xrandr", "--display", _display, "--fb", $"{before.Width}x{before.Height}");
    }

    private void Run(params string[] arguments)
    {
        (int code, string output) = runner.Run("xrandr", ["--display", _display, .. arguments]);
        if (code != 0)
        {
            throw new InvalidOperationException($"xrandr {string.Join(' ', arguments)} failed: {output.Trim()}");
        }
    }
}
