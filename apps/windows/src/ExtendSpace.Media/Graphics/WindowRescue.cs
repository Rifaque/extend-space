using System.Runtime.InteropServices;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Media.Input;

namespace ExtendSpace.Media.Graphics;

/// <summary>
/// Brings windows back from the virtual display to the main display when the user presses the button
/// (ES-141). Only visible, ownerless, non-tool top-level windows whose centre is on the virtual display
/// are moved; nothing runs on its own, and display settings are not touched.
/// </summary>
public sealed class WindowRescue(IVirtualDisplayCatalog catalog) : IWindowRescue
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint GwOwner = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private const int DwmaCloaked = 14;

    public int BringBack(string virtualDeviceName)
    {
        IReadOnlyList<DisplayTarget> targets = catalog.Targets();
        if (VirtualDisplayModes.Guard(targets, virtualDeviceName) is not null)
        {
            return 0;
        }

        ScreenRect? virtualRect = targets.First(t => string.Equals(t.GdiDeviceName, virtualDeviceName, StringComparison.OrdinalIgnoreCase)).Bounds;
        // The main display: the physical monitor at the desktop origin.
        ScreenRect? primary = targets.Select(t => t.Bounds).FirstOrDefault(b => b is { Left: 0, Top: 0 });
        if (virtualRect is null || primary is null)
        {
            return 0;
        }

        int moved = 0;
        using PhysicalPixels physical = PhysicalPixels.Enter();
        var windows = new List<nint>();
        EnumWindows((window, _) => { windows.Add(window); return true; }, 0);
        foreach (nint window in windows)
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, GwOwner) != 0
                || (GetWindowLongPtrW(window, GwlExStyle) & WsExToolWindow) != 0 || Cloaked(window)
                || !GetWindowRect(window, out Rect rect))
            {
                continue;
            }

            var bounds = new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (WindowRescuePlan.Destination(bounds, virtualRect.Value, primary.Value) is not { } to)
            {
                continue;
            }

            bool maximized = IsZoomed(window);
            if (maximized)
            {
                ShowWindow(window, SwRestore);
                GetWindowRect(window, out rect);
                bounds = new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                to = WindowRescuePlan.Destination(bounds, virtualRect.Value, primary.Value) ?? new ScreenPoint(primary.Value.Left, primary.Value.Top);
            }

            if (SetWindowPos(window, 0, to.X, to.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
            {
                moved++;
            }

            if (maximized)
            {
                ShowWindow(window, SwMaximize);
            }
        }

        return moved;
    }

    private static bool Cloaked(nint window) => DwmGetWindowAttribute(window, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
