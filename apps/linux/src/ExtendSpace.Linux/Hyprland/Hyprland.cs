using System.Globalization;
using System.Text.Json;
using ExtendSpace.Core.Input;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux.Hyprland;

/// <summary>One Hyprland output as <c>hyprctl -j monitors</c> reports it (pixels are the mode's, before scaling).</summary>
public sealed record HyprlandMonitor(int Id, string Name, int Width, int Height, double RefreshHz, int X, int Y, double Scale, bool Focused);

/// <summary>Reading and changing Hyprland's outputs through its own IPC, <c>hyprctl</c> (D105).</summary>
public static class Hyprctl
{
    public static IReadOnlyList<HyprlandMonitor>? ParseMonitors(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return [.. document.RootElement.EnumerateArray().Select(m => new HyprlandMonitor(
                m.GetProperty("id").GetInt32(),
                m.GetProperty("name").GetString() ?? string.Empty,
                m.GetProperty("width").GetInt32(),
                m.GetProperty("height").GetInt32(),
                m.TryGetProperty("refreshRate", out JsonElement r) ? r.GetDouble() : 0,
                m.GetProperty("x").GetInt32(),
                m.GetProperty("y").GetInt32(),
                m.TryGetProperty("scale", out JsonElement s) ? s.GetDouble() : 1,
                m.TryGetProperty("focused", out JsonElement f) && f.GetBoolean()))];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static IReadOnlyList<HyprlandMonitor>? Monitors(ICommandRunner runner)
    {
        (int code, string output) = runner.Run("hyprctl", "-j", "monitors");
        return code == 0 ? ParseMonitors(output) : null;
    }
}

/// <summary>
/// The Mirror list on Hyprland (D105): its outputs by name, the names the ScreenCast portal reports as
/// <c>mapping_id</c>. ExtendSpace's own Extend output is left out, as on every platform. The first
/// output (id 0) stands for the main display.
/// </summary>
public sealed class HyprlandMonitorSource(ICommandRunner runner) : IX11MonitorSource
{
    public IReadOnlyList<X11Monitor>? Monitors()
    {
        IReadOnlyList<HyprlandMonitor>? monitors = Hyprctl.Monitors(runner);
        if (monitors is null)
        {
            return null;
        }

        // ExtendSpace's own Extend output is never offered for Mirror.
        List<HyprlandMonitor> physical = [.. monitors.Where(m => !HyprlandVirtualDisplay.IsOurs(m.Name))];
        int main = physical.Select(m => m.Id).DefaultIfEmpty(-1).Min();
        return [.. physical.Select(m => new X11Monitor(m.Name, m.Id == main, new ScreenRect(m.X, m.Y, m.Width, m.Height)))];
    }
}

/// <summary>
/// Extend on Hyprland (D105): a headless output, which Hyprland treats as a real monitor (windows,
/// workspaces, the cursor move onto it), created for the session with the tablet's size at 60 Hz and
/// placed to the right of the physical outputs, which keep their modes and positions. It is captured
/// through the ScreenCast portal like any monitor, so the user still consents in the desktop's dialog.
/// Removed when the session ends; one left by a crash is removed at the next start (it is ours by name).
/// Nothing is written to the user's configuration: the mode rule lives only in the running compositor.
/// </summary>
public sealed class HyprlandVirtualDisplay(ICommandRunner runner, Action<string>? log = null)
{
    public const string OutputName = "EXTENDSPACE-1";
    private bool _created;

    public static bool IsOurs(string outputName) => outputName.StartsWith("EXTENDSPACE-", StringComparison.Ordinal);

    /// <summary>The monitor rule: Lua for Hyprland 0.53 and later (hyprland.lua), the keyword before.</summary>
    public static (string Lua, string Keyword) Rule(string name, int width, int height, int refreshHz) => (
        string.Create(CultureInfo.InvariantCulture, $"hl.monitor({{ output = \"{name}\", mode = \"{width}x{height}@{refreshHz}\", position = \"auto-right\", scale = \"1\" }})"),
        string.Create(CultureInfo.InvariantCulture, $"{name},{width}x{height}@{refreshHz},auto-right,1"));

    /// <summary>Removes outputs a previous run left behind (a crash, a killed process).</summary>
    public int RemoveLeftovers()
    {
        int removed = 0;
        foreach (HyprlandMonitor monitor in Hyprctl.Monitors(runner) ?? [])
        {
            if (IsOurs(monitor.Name) && runner.Run("hyprctl", "output", "remove", monitor.Name).ExitCode == 0)
            {
                log?.Invoke($"hyprland: removed leftover output {monitor.Name}");
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Creates the output with the given mode; returns it as Hyprland reports it, or throws.</summary>
    public HyprlandMonitor Create(int width, int height, int refreshHz = 60)
    {
        RemoveLeftovers();
        (int code, string output) = runner.Run("hyprctl", "output", "create", "headless", OutputName);
        if (code != 0 || !output.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Hyprland could not create an output: {output.Trim()}");
        }

        _created = true;
        (string lua, string keyword) = Rule(OutputName, width, height, refreshHz);
        (int evalCode, string evalOutput) = runner.Run("hyprctl", "eval", lua);
        if (evalCode != 0 || !evalOutput.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            runner.Run("hyprctl", "keyword", "monitor", keyword);
        }

        // The mode applies asynchronously; read it back.
        HyprlandMonitor? made = null;
        for (int i = 0; i < 20; i++)
        {
            made = Hyprctl.Monitors(runner)?.FirstOrDefault(m => m.Name == OutputName);
            if (made is { } m && m.Width == width && m.Height == height)
            {
                break;
            }

            Thread.Sleep(50);
        }

        if (made is null)
        {
            Remove();
            throw new InvalidOperationException("Hyprland created the output but does not list it.");
        }

        if (made.Width != width || made.Height != height)
        {
            log?.Invoke($"hyprland: asked for {width} x {height}, the output is {made.Width} x {made.Height}");
        }

        log?.Invoke($"hyprland: output {OutputName} {made.Width} x {made.Height} at {made.RefreshHz:0.#} Hz, position {made.X},{made.Y}");
        return made;
    }

    /// <summary>Removes the output; safe to call when nothing was created.</summary>
    public void Remove()
    {
        if (!_created)
        {
            return;
        }

        _created = false;
        (int code, string output) = runner.Run("hyprctl", "output", "remove", OutputName);
        log?.Invoke(code == 0 ? $"hyprland: removed output {OutputName}" : $"hyprland: removing {OutputName} failed: {output.Trim()}");
    }
}
