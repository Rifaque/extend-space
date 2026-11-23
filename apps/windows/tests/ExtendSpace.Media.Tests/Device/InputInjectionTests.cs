using ExtendSpace.Core.Input;
using ExtendSpace.Media.Input;

namespace ExtendSpace.Media.Tests.Device;

/// <summary>
/// The real Windows injectors on this PC (D82). They act on the PC's screen, so they are gated:
/// <c>EXTENDSPACE_INPUT_TESTS=1</c>, with <c>EXTENDSPACE_INPUT_POINT=x,y</c> on a window put there to
/// receive them. Whether the window received them is checked by whoever put it there.
/// </summary>
public sealed class InputInjectionTests
{
    private static ScreenPoint Target()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_TESTS") == "1", "input injection acts on the screen: set EXTENDSPACE_INPUT_TESTS=1 and EXTENDSPACE_INPUT_POINT=x,y over a test window");
        string[] point = (Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_POINT") ?? throw new InvalidOperationException("EXTENDSPACE_INPUT_POINT is not set")).Split(',');
        return new ScreenPoint(int.Parse(point[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(point[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_synthetic_touch_tap_is_accepted_by_Windows()
    {
        ScreenPoint point = Target();
        using var touch = new SyntheticTouchInjector();
        Assert.True(touch.Inject([new InjectedContact(1, point, ContactState.Down)]), $"down refused: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(touch.Inject([new InjectedContact(1, point, ContactState.Update)]), "update refused");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(touch.Inject([new InjectedContact(1, point, ContactState.Up)]), "up refused");
    }

    [Fact]
    public void Where_Windows_maps_each_pointer_device()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_TESTS") == "1", "creates a synthetic touch device: set EXTENDSPACE_INPUT_TESTS=1");
        using var touch = new SyntheticTouchInjector();
        foreach (PointerDeviceMapping device in PointerDevices.List())
        {
            TestContext.Current.SendDiagnosticMessage($"{device.Type} '{device.Name}' device={device.DeviceRect} display={device.DisplayRect} monitor={device.Monitor}");
            Console.WriteLine($"DEVICE {device.Type} '{device.Name}' device={device.DeviceRect} display={device.DisplayRect} monitor={device.Monitor}");
        }
    }

    [Fact]
    public async Task A_mouse_click_is_sent()
    {
        ScreenPoint point = Target();
        var mouse = new SendInputMouseInjector();
        mouse.Move(point);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        mouse.Button(MouseButton.Left, down: true);
        mouse.Button(MouseButton.Left, down: false);
    }

    /// <summary>
    /// ES-135: moves the real cursor to every 7th pixel of <c>EXTENDSPACE_INPUT_RECT=x,y,w,h</c> (put
    /// it on a display nobody is using, such as a virtual one) and reports each point where the
    /// cursor did not land exactly; with <c>EXTENDSPACE_INPUT_CLICKS=n</c> it also clicks n points
    /// spread over the rectangle, 300 ms apart, for a test window there to count.
    /// </summary>
    [Fact]
    public async Task The_cursor_lands_on_the_exact_pixel_aimed_at()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_TESTS") == "1", "moves the cursor: set EXTENDSPACE_INPUT_TESTS=1 and EXTENDSPACE_INPUT_RECT=x,y,w,h");
        int[] r = (Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_RECT") ?? throw new InvalidOperationException("EXTENDSPACE_INPUT_RECT is not set"))
            .Split(',').Select(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var mouse = new SendInputMouseInjector();
        var misses = new List<string>();
        int checkedPoints = 0;
        for (int y = r[1]; y < r[1] + r[3]; y += 37)
        {
            for (int x = r[0]; x < r[0] + r[2]; x += 7)
            {
                mouse.Move(new ScreenPoint(x, y));
                CursorPosition(out int cx, out int cy);
                checkedPoints++;
                if (cx != x || cy != y)
                {
                    misses.Add($"({x},{y})->({cx},{cy})");
                }
            }
        }

        int clicks = int.TryParse(Environment.GetEnvironmentVariable("EXTENDSPACE_INPUT_CLICKS"), out int n) ? n : 0;
        for (int i = 0; i < clicks; i++)
        {
            var point = new ScreenPoint(r[0] + 20 + (i * 37 % Math.Max(1, r[2] - 40)), r[1] + 60 + (i * 53 % Math.Max(1, r[3] - 80)));
            mouse.Move(point);
            mouse.Button(MouseButton.Left, down: true);
            mouse.Button(MouseButton.Left, down: false);
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        TestContext.Current.SendDiagnosticMessage($"CURSOR checked {checkedPoints} points, {misses.Count} off: {string.Join(' ', misses.Take(40))}");
        Assert.Empty(misses);
    }

    private static void CursorPosition(out int x, out int y)
    {
        // Read per-monitor DPI aware: otherwise Windows scales the answer on scaled monitors (it read
        // 0.8 x the true offset at 125 % before this), though the cursor itself was exact.
        using var physical = ExtendSpace.Media.Input.PhysicalPixels.Enter();
        Assert.True(GetPhysicalCursorPos(out Point point));
        (x, y) = (point.X, point.Y);
    }

    private struct Point
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPhysicalCursorPos(out Point point);
}
