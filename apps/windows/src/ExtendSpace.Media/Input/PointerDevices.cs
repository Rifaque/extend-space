using ExtendSpace.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.Pointer;

namespace ExtendSpace.Media.Input;

/// <summary>A pointer device as Windows maps it: the device's own coordinate rectangle and the display rectangle it lands on.</summary>
public sealed record PointerDeviceMapping(string Name, string Type, ScreenRect DeviceRect, ScreenRect DisplayRect, string? Monitor);

/// <summary>Lists the pointer devices Windows knows and where each is mapped (for diagnosing touch landing on the wrong display).</summary>
public static unsafe class PointerDevices
{
    public static IReadOnlyList<PointerDeviceMapping> List()
    {
        using PhysicalPixels physical = PhysicalPixels.Enter();
        uint count = 0;
        if (!PInvoke.GetPointerDevices(ref count, null) || count == 0)
        {
            return [];
        }

        var devices = new POINTER_DEVICE_INFO[count];
        if (!PInvoke.GetPointerDevices(ref count, devices))
        {
            return [];
        }

        var result = new List<PointerDeviceMapping>();
        foreach (POINTER_DEVICE_INFO device in devices.Take((int)count))
        {
            ScreenRect deviceRect = default, displayRect = default;
            if (GetPointerDeviceRects(device.device, out RECT pointer, out RECT display))
            {
                deviceRect = new ScreenRect(pointer.left, pointer.top, pointer.right - pointer.left, pointer.bottom - pointer.top);
                displayRect = new ScreenRect(display.left, display.top, display.right - display.left, display.bottom - display.top);
            }

            string? monitor = null;
            var info = default(MONITORINFOEXW);
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!device.monitor.IsNull && PInvoke.GetMonitorInfo(device.monitor, (MONITORINFO*)&info))
            {
                monitor = info.szDevice.ToString();
            }

            result.Add(new PointerDeviceMapping(device.productString.ToString(), device.pointerDeviceType.ToString(), deviceRect, displayRect, monitor));
        }

        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetPointerDeviceRects(HANDLE device, out RECT pointerDeviceRect, out RECT displayRect);
}
