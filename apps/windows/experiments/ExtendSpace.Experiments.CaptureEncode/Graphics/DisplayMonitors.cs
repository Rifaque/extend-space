using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace ExtendSpace.Experiments.CaptureEncode.Graphics;

/// <param name="Index">Position in the order Windows enumerates monitors; what <c>--monitor</c> takes.</param>
/// <param name="DeviceName">For example <c>\\.\DISPLAY1</c>.</param>
internal sealed record DisplayMonitor(
    int Index,
    nint Handle,
    string DeviceName,
    bool IsPrimary,
    int Left,
    int Top,
    int Width,
    int Height);

internal static class DisplayMonitors
{
    /// <summary>
    /// The monitors attached to the desktop right now, in pixels. Call
    /// <see cref="EnablePerMonitorDpiAwareness"/> first or the sizes are scaled by the display's DPI setting.
    /// </summary>
    public static unsafe IReadOnlyList<DisplayMonitor> Enumerate()
    {
        var monitors = new List<DisplayMonitor>();

        BOOL OnMonitor(HMONITOR handle, HDC hdc, RECT* clip, LPARAM data)
        {
            var info = default(MONITORINFOEXW);
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
            {
                RECT bounds = info.monitorInfo.rcMonitor;
                monitors.Add(new DisplayMonitor(
                    monitors.Count,
                    handle,
                    info.szDevice.ToString(),
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0,
                    bounds.left,
                    bounds.top,
                    bounds.right - bounds.left,
                    bounds.bottom - bounds.top));
            }

            return true;
        }

        if (!PInvoke.EnumDisplayMonitors(default, (RECT?)null, OnMonitor, default))
        {
            throw new InvalidOperationException("Windows could not enumerate the display monitors (EnumDisplayMonitors failed).");
        }

        return monitors;
    }

    public static void EnablePerMonitorDpiAwareness()
    {
        // Fails harmlessly if awareness was already set; the capture item's own size is what the
        // pipeline uses, so this only affects the sizes shown by 'list'.
        PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}
