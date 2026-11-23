using System.Runtime.InteropServices;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Linux.X11;

/// <summary>One RandR 1.5 monitor.</summary>
public sealed record X11Monitor(string Name, bool IsPrimary, ScreenRect Bounds);

/// <summary>Reads the X server's monitors; null when there is no X display.</summary>
public interface IX11MonitorSource
{
    IReadOnlyList<X11Monitor>? Monitors();
}

/// <summary>XRRGetMonitors on <c>$DISPLAY</c>; opens and closes its own connection each time (quick, and safe on any thread).</summary>
public sealed class XrandrMonitorSource(string? display = null) : IX11MonitorSource
{
    public string? Display { get; } = display ?? Environment.GetEnvironmentVariable("DISPLAY");

    public unsafe IReadOnlyList<X11Monitor>? Monitors()
    {
        if (string.IsNullOrEmpty(Display))
        {
            return null;
        }

        nint connection;
        try
        {
            connection = X11Native.OpenDisplay(Display);
        }
        catch (DllNotFoundException)
        {
            return null;
        }

        if (connection == 0)
        {
            return null;
        }

        try
        {
            X11Native.MonitorInfo* monitors = X11Native.GetMonitors(connection, X11Native.DefaultRootWindow(connection), 1, out int count);
            if (monitors == null)
            {
                return [];
            }

            var result = new List<X11Monitor>(count);
            for (int i = 0; i < count; i++)
            {
                nint name = X11Native.GetAtomName(connection, monitors[i].Name);
                string text = Marshal.PtrToStringUTF8(name) ?? $"monitor-{i}";
                X11Native.Free(name);
                result.Add(new X11Monitor(text, monitors[i].Primary != 0, new ScreenRect(monitors[i].X, monitors[i].Y, monitors[i].Width, monitors[i].Height)));
            }

            X11Native.FreeMonitors(monitors);
            return result;
        }
        finally
        {
            X11Native.CloseDisplay(connection);
        }
    }
}

/// <summary>
/// The X11 displays for Mirror (Core's <see cref="IDisplayCatalog"/>) and the rectangle input aims at
/// (Core's <see cref="IMonitorLocator"/>). Names are the RandR monitor names (<c>eDP-1</c>,
/// <c>HDMI-1</c>); the virtual monitor (D99) is left out of the Mirror list, as on Windows.
/// </summary>
public sealed class X11Displays(IX11MonitorSource source) : IDisplayCatalog, IMonitorLocator
{
    public IReadOnlyList<DisplayInfo> List() =>
        [.. (source.Monitors() ?? [])
            .Where(m => !X11VirtualDisplay.IsOurs(m.Name))
            .Select(m => new DisplayInfo(m.Name, m.IsPrimary, m.Bounds.Width, m.Bounds.Height, null))];

    public ScreenRect? Locate(string? deviceName)
    {
        IReadOnlyList<X11Monitor> monitors = source.Monitors() ?? [];
        X11Monitor? match = deviceName is null
            ? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault()
            : monitors.FirstOrDefault(m => m.Name == deviceName)
              ?? (deviceName == LinuxSourceNames.Virtual ? monitors.FirstOrDefault(m => X11VirtualDisplay.IsOurs(m.Name)) : null);
        return match?.Bounds;
    }
}

/// <summary>
/// Mouse mode on X11 (D100): XTest pointer events in root-window coordinates. Mouse emulation; X11
/// has no way to inject touch without uinput, which ExtendSpace does not use.
/// </summary>
public sealed class XTestMouseInjector : IMouseInjector, IDisposable
{
    private readonly Lock _gate = new();
    private nint _display;

    public XTestMouseInjector(string? display = null)
    {
        string? name = display ?? Environment.GetEnvironmentVariable("DISPLAY");
        try
        {
            _display = string.IsNullOrEmpty(name) ? 0 : X11Native.OpenDisplay(name);
            if (_display != 0 && X11Native.TestQueryExtension(_display, out _, out _, out _, out _) == 0)
            {
                X11Native.CloseDisplay(_display);
                _display = 0;
            }
        }
        catch (DllNotFoundException)
        {
            _display = 0;
        }
    }

    public bool IsAvailable => _display != 0;

    public void Move(ScreenPoint point)
    {
        lock (_gate)
        {
            if (_display != 0)
            {
                X11Native.TestFakeMotion(_display, -1, point.X, point.Y, 0);
                X11Native.Flush(_display);
            }
        }
    }

    public void Button(MouseButton button, bool down) => Press(button == MouseButton.Left ? 1u : 3u, down);

    public void Wheel(int vertical, int horizontal)
    {
        // X11 wheels are buttons: 4 up, 5 down, 6 left, 7 right; one click per 120 units.
        for (int i = 0; i < Math.Abs(vertical) / 120; i++)
        {
            Click(vertical > 0 ? 4u : 5u);
        }

        for (int i = 0; i < Math.Abs(horizontal) / 120; i++)
        {
            Click(horizontal > 0 ? 7u : 6u);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_display != 0)
            {
                X11Native.CloseDisplay(_display);
                _display = 0;
            }
        }
    }

    private void Click(uint button)
    {
        Press(button, true);
        Press(button, false);
    }

    private void Press(uint button, bool down)
    {
        lock (_gate)
        {
            if (_display != 0)
            {
                X11Native.TestFakeButton(_display, button, down ? 1 : 0, 0);
                X11Native.Flush(_display);
            }
        }
    }
}
