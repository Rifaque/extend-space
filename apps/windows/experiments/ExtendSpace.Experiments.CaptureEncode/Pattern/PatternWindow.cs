using System.Runtime.InteropServices;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ExtendSpace.Experiments.CaptureEncode.Pattern;

/// <summary>
/// A borderless window that draws the test pattern with Direct3D 11 and presents it through a DXGI
/// swap chain, synchronised to the display's refresh. It is created, drawn and pumped on one thread.
///
/// Drawing uses nothing but rectangle clears, so there are no shaders, fonts or other dependencies.
/// </summary>
internal sealed unsafe class PatternWindow : IDisposable
{
    private const string ClassName = "ExtendSpaceTestPattern";

    /// <summary>DXGI_STATUS_OCCLUDED: the present succeeded but nothing was shown, because the window is not visible.</summary>
    private const int StatusOccluded = 0x087A0001;

    // Kept in a static so that the delegate Windows calls is never collected.
    private static readonly WNDPROC Procedure = WindowProcedure;

    private static readonly float[][] Colours =
    [
        [0.00f, 0.00f, 0.00f, 1f], // Black
        [1.00f, 1.00f, 1.00f, 1f], // White
        [0.85f, 0.10f, 0.10f, 1f], // Red
        [0.10f, 0.75f, 0.15f, 1f], // Green
        [0.10f, 0.20f, 0.90f, 1f], // Blue
        [0.95f, 0.90f, 0.10f, 1f], // Yellow
        [0.10f, 0.85f, 0.90f, 1f], // Cyan
        [0.85f, 0.15f, 0.85f, 1f], // Magenta
        [0.12f, 0.12f, 0.12f, 1f], // DarkGrey
        [0.50f, 0.50f, 0.50f, 1f], // MidGrey
    ];

    private readonly List<(PatternColor Color, Box Box)> _boxes = [];
    private readonly List<RECT>[] _byColour = [.. Colours.Select(_ => new List<RECT>())];
    private readonly HINSTANCE _instance;

    private HWND _window;
    private GraphicsDevice? _graphics;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _target;
    private bool _classRegistered;

    public PatternWindow(DisplayMonitor monitor, PatternOptions options)
    {
        _instance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(PatternWindow).Module));
        Model = options.Model;

        (int width, int height) = options.FullScreen
            ? (monitor.Width, monitor.Height)
            : (Math.Min(options.Size.Width, monitor.Width), Math.Min(options.Size.Height, monitor.Height));
        Width = width;
        Height = height;
        Left = monitor.Left + ((monitor.Width - width) / 2);
        Top = monitor.Top + ((monitor.Height - height) / 2);
        CoversMonitor = width == monitor.Width && height == monitor.Height;

        try
        {
            CreateWindow(options.Topmost);
            CreateSwapChain();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public bool CoversMonitor { get; }

    public PresentModel Model { get; }

    public string AdapterName => _graphics?.AdapterName ?? "unknown";

    /// <summary>Handles the window's messages. Returns false once the window has been closed.</summary>
    public bool PumpMessages()
    {
        while (PInvoke.PeekMessage(out MSG message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            if (message.message == PInvoke.WM_QUIT)
            {
                _window = HWND.Null;
                return false;
            }

            PInvoke.TranslateMessage(in message);
            PInvoke.DispatchMessage(in message);
        }

        return true;
    }

    /// <summary>Draws the picture for one frame into the back buffer.</summary>
    public void Draw(long frame, long elapsedMilliseconds)
    {
        PatternLayout.Build(Width, Height, frame, elapsedMilliseconds, _boxes);
        foreach (List<RECT> list in _byColour)
        {
            list.Clear();
        }

        // Boxes of one colour are cleared in one call. Later boxes never have to cover earlier
        // ones of a different colour except the panels, which are drawn first, in black.
        var context = (ID3D11DeviceContext1)_graphics!.Context;
        context.ClearRenderTargetView(_target!, Colours[(int)PatternColor.DarkGrey]);

        foreach ((PatternColor colour, Box box) in _boxes)
        {
            if (box.Right <= box.Left || box.Bottom <= box.Top)
            {
                continue;
            }

            var rectangle = new RECT(box.Left, box.Top, box.Right, box.Bottom);
            if (colour == PatternColor.Black && box.Left == 0 && box.Right == Width)
            {
                // A full-width black panel: something else is drawn on top of it.
                context.ClearView(_target!, Colours[(int)PatternColor.Black], [rectangle]);
            }
            else
            {
                _byColour[(int)colour].Add(rectangle);
            }
        }

        for (int colour = 0; colour < _byColour.Length; colour++)
        {
            if (_byColour[colour].Count > 0)
            {
                context.ClearView(_target!, Colours[colour], CollectionsMarshal.AsSpan(_byColour[colour]));
            }
        }
    }

    /// <summary>
    /// Presents the back buffer, waiting for the display's refresh. Returns false if Windows showed
    /// nothing because the window is covered or minimised.
    /// </summary>
    public bool Present(int syncInterval)
    {
        HRESULT result = _swapChain!.Present((uint)syncInterval, 0);
        if (result.Value == StatusOccluded)
        {
            return false;
        }

        result.ThrowOnFailure();
        return true;
    }

    /// <summary>DXGI's own count of presents and of the refreshes they landed on, where it keeps one.</summary>
    public DXGI_FRAME_STATISTICS? FrameStatistics()
    {
        DXGI_FRAME_STATISTICS statistics;
        return _swapChain!.GetFrameStatistics(&statistics).Succeeded ? statistics : null;
    }

    public void Dispose()
    {
        ComReference.Release(ref _target);
        ComReference.Release(ref _swapChain);
        _graphics?.Dispose();
        _graphics = null;

        if (!_window.IsNull)
        {
            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
        }

        if (_classRegistered)
        {
            fixed (char* className = ClassName)
            {
                PInvoke.UnregisterClass(className, _instance);
            }

            _classRegistered = false;
        }
    }

    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_KEYDOWN when (VIRTUAL_KEY)(ushort)wParam.Value == VIRTUAL_KEY.VK_ESCAPE:
            case PInvoke.WM_RBUTTONUP:
                PInvoke.DestroyWindow(window);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void CreateWindow(bool topmost)
    {
        fixed (char* className = ClassName)
        fixed (char* title = "ExtendSpace test pattern (Esc or right-click closes)")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Procedure,
                hInstance = _instance,
                hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW),
                lpszClassName = className,
            };

            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                throw new InvalidOperationException($"The test pattern's window class could not be registered (error {Marshal.GetLastPInvokeError()}).");
            }

            _classRegistered = true;

            // Shown without taking the keyboard focus, so that it does not interrupt whoever is typing.
            _window = PInvoke.CreateWindowEx(
                topmost ? WINDOW_EX_STYLE.WS_EX_TOPMOST : 0,
                className,
                title,
                WINDOW_STYLE.WS_POPUP,
                Left,
                Top,
                Width,
                Height,
                HWND.Null,
                default,
                _instance,
                null);
            if (_window.IsNull)
            {
                throw new InvalidOperationException($"The test pattern's window could not be created (error {Marshal.GetLastPInvokeError()}).");
            }

            PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }
    }

    private void CreateSwapChain()
    {
        _graphics = GraphicsDevice.Create();

        // The swap chain has to come from the factory that the device's adapter came from.
        ((IDXGIDevice)_graphics.Device).GetAdapter(out IDXGIAdapter adapter);
        object? factory = null;
        try
        {
            Guid factoryId = typeof(IDXGIFactory2).GUID;
            adapter.GetParent(&factoryId, out void* parent);
            factory = Marshal.GetObjectForIUnknown((nint)parent);
            Marshal.Release((nint)parent);

            bool flip = Model == PresentModel.Flip;
            var description = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = flip ? 2u : 1u,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = flip ? DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD : DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            ((IDXGIFactory2)factory).CreateSwapChainForHwnd(_graphics.Device, _window, in description, null, null, out _swapChain);
        }
        finally
        {
            ComReference.Release(factory);
            ComReference.Release(adapter);
        }

        Guid textureId = typeof(ID3D11Texture2D).GUID;
        _swapChain.GetBuffer(0, &textureId, out void* surface);
        var backBuffer = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)surface);
        Marshal.Release((nint)surface);
        try
        {
            _graphics.Device.CreateRenderTargetView(backBuffer, null, out _target);
        }
        finally
        {
            ComReference.Release(backBuffer);
        }
    }
}
