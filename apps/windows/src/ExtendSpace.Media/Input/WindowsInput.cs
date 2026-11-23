using System.ComponentModel;
using System.Runtime.InteropServices;
using ExtendSpace.Core.Input;
using ExtendSpace.Media.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ExtendSpace.Media.Input;

/// <summary>
/// Runs Windows calls in physical pixels whatever the calling process's DPI awareness: injected
/// coordinates and monitor rectangles must not be scaled by the display's scaling setting. Found on
/// hardware (125 % scaling): an unaware caller's clicks landed 1.25 times too far from the origin.
/// </summary>
internal readonly struct PhysicalPixels : IDisposable
{
    private readonly DPI_AWARENESS_CONTEXT _previous;

    private PhysicalPixels(DPI_AWARENESS_CONTEXT previous) => _previous = previous;

    public static PhysicalPixels Enter() => new(PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

    public void Dispose()
    {
        if (_previous != default)
        {
            PInvoke.SetThreadDpiAwarenessContext(_previous);
        }
    }
}

/// <summary>
/// Real touch for Touch mode (D82): one synthetic touch device (Windows 10 1809+), up to 10
/// contacts, created on first use. Coordinates are virtual-screen pixels.
/// </summary>
public sealed unsafe class SyntheticTouchInjector : ITouchInjector
{
    private const uint MaxContacts = 10;
    private HSYNTHETICPOINTERDEVICE _device;

    public SyntheticTouchInjector()
    {
        _device = PInvoke.CreateSyntheticPointerDevice(POINTER_INPUT_TYPE.PT_TOUCH, MaxContacts, POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_DEFAULT);
        if (_device.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not create a synthetic touch device.");
        }
    }

    public bool Inject(IReadOnlyList<InjectedContact> frame)
    {
        if (_device.IsNull || frame.Count == 0 || frame.Count > MaxContacts)
        {
            return false;
        }

        using PhysicalPixels physical = PhysicalPixels.Enter();

        // "ptPixelLocation ... relative to top left of the virtual screen" (Microsoft Learn): the
        // origin is the virtual screen's top-left corner, not the main display's. With a display to
        // the left of or above the main one the two differ; found on hardware, where touches meant for
        // the main monitor landed on a laptop panel to its left.
        int originX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int originY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        Span<POINTER_TYPE_INFO> infos = stackalloc POINTER_TYPE_INFO[frame.Count];
        for (int index = 0; index < frame.Count; index++)
        {
            InjectedContact screen = frame[index];
            InjectedContact contact = screen with { Point = VirtualScreen.ToTouchSpace(screen.Point, originX, originY) };
            var touch = default(POINTER_TOUCH_INFO);
            touch.pointerInfo.pointerType = POINTER_INPUT_TYPE.PT_TOUCH;
            touch.pointerInfo.pointerId = contact.PointerId;
            touch.pointerInfo.ptPixelLocation = new System.Drawing.Point(contact.Point.X, contact.Point.Y);
            touch.pointerInfo.pointerFlags = contact.State switch
            {
                ContactState.Down => POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                ContactState.Update => POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                ContactState.Up => POINTER_FLAGS.POINTER_FLAG_UP,
                _ => POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_CANCELED,
            };
            touch.touchMask = 0x1; // TOUCH_MASK_CONTACTAREA: rcContact is set
            touch.rcContact = new RECT(contact.Point.X - 2, contact.Point.Y - 2, contact.Point.X + 2, contact.Point.Y + 2);
            infos[index].type = POINTER_INPUT_TYPE.PT_TOUCH;
            infos[index].Anonymous.touchInfo = touch;
        }

        fixed (POINTER_TYPE_INFO* first = infos)
        {
            if (PInvoke.InjectSyntheticPointerInput(_device, first, (uint)frame.Count))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (!_device.IsNull)
        {
            PInvoke.DestroySyntheticPointerDevice(_device);
            _device = default;
        }
    }
}

/// <summary>Coordinate conversions for the injection APIs, separated so that they can be tested.</summary>
public static class VirtualScreen
{
    /// <summary>A desktop point as synthetic touch wants it: relative to the virtual screen's top-left corner.</summary>
    public static ScreenPoint ToTouchSpace(ScreenPoint desktop, int virtualLeft, int virtualTop) => new(desktop.X - virtualLeft, desktop.Y - virtualTop);
}

/// <summary>Mouse input for Mouse mode (D82), through SendInput on the whole virtual desktop.</summary>
public sealed unsafe class SendInputMouseInjector : IMouseInjector
{
    public void Move(ScreenPoint point)
    {
        using PhysicalPixels physical = PhysicalPixels.Enter();
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN) - 1);
        int height = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN) - 1);
        Send(new MOUSEINPUT
        {
            dx = (int)Math.Round((point.X - left) * 65535.0 / width),
            dy = (int)Math.Round((point.Y - top) * 65535.0 / height),
            dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
        });
    }

    public void Button(MouseButton button, bool down) => Send(new MOUSEINPUT
    {
        dwFlags = (button, down) switch
        {
            (MouseButton.Left, true) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
            (_, true) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN,
            _ => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP,
        },
    });

    public void Wheel(int vertical, int horizontal)
    {
        if (vertical != 0)
        {
            Send(new MOUSEINPUT { mouseData = unchecked((uint)vertical), dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL });
        }

        if (horizontal != 0)
        {
            Send(new MOUSEINPUT { mouseData = unchecked((uint)horizontal), dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL });
        }
    }

    private static void Send(MOUSEINPUT mouse)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        input.Anonymous.mi = mouse;
        PInvoke.SendInput(new ReadOnlySpan<INPUT>(ref input), sizeof(INPUT));
    }
}

/// <summary>The mirrored monitor's rectangle, read from Windows each time it is asked (D83).</summary>
public sealed class DesktopMonitorLocator : IMonitorLocator
{
    public ScreenRect? Locate(string? deviceName)
    {
        IReadOnlyList<DisplayMonitor> monitors;
        using PhysicalPixels physical = PhysicalPixels.Enter();
        try
        {
            monitors = DisplayMonitors.Enumerate();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        DisplayMonitor? monitor = deviceName is null
            ? monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? monitors.FirstOrDefault()
            : monitors.FirstOrDefault(candidate => string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        return monitor is null ? null : new ScreenRect(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
    }
}
