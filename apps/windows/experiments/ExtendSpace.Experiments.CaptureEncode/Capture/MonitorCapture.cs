using System.Runtime.InteropServices;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using ExtendSpace.Experiments.CaptureEncode.Measurement;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;

namespace ExtendSpace.Experiments.CaptureEncode.Capture;

internal sealed record CaptureCounters(
    long Delivered,
    long SkippedByPacer,
    long ReplacedBeforeTaken,
    long DiscardedAtStop,
    TimeSpan? FirstFrameTime,
    TimeSpan? LastFrameTime);

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture and keeps only the newest accepted frame for
/// the encoder. The hand-off holds a single frame, so memory does not grow when the encoder is
/// slower than the display; a frame that is overtaken is released and counted.
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    /// <summary>
    /// One frame waiting for the encoder, one being converted, and one free so that a new frame can
    /// still arrive and overtake the waiting one. If the pool were ever empty Windows would skip
    /// frames without telling the application, and those could not be counted.
    /// </summary>
    public const int FramePoolBuffers = 3;

    private static readonly Guid GraphicsCaptureItemInterfaceId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    private readonly Lock _gate = new();
    private readonly FramePacer _pacer;
    private readonly DurationSeries _deliveryIntervals = new();
    private readonly Action _wake;

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _pending;
    private bool _running;
    private string? _failure;

    private long _delivered;
    private long _skippedByPacer;
    private long _replaced;
    private long _discardedAtStop;
    private TimeSpan? _firstFrameTime;
    private TimeSpan? _lastFrameTime;

    /// <param name="wake">Called, possibly on a capture thread, whenever there is something new to look at.</param>
    public MonitorCapture(GraphicsDevice graphics, DisplayMonitor monitor, CaptureOptions options, Action wake)
    {
        _wake = wake;
        _pacer = new FramePacer(options.FrameInterval);

        try
        {
            _item = CreateItem(monitor);
            Size = _item.Size;
            if (Size.Width <= 0 || Size.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"Monitor {monitor.DeviceName} reports a capture size of {Size.Width} x {Size.Height}; it is not available for capture.");
            }

            _device = graphics.CreateWinRTDevice();
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBuffers,
                Size);
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = options.CaptureCursor;
            BorderDescription = ConfigureBorder(_session, options.RequestBorderless);
            RateLimitDescription = ConfigureRateLimit(_session, options.FrameInterval);

            _framePool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The size the capture item reports, in pixels.</summary>
    public SizeInt32 Size { get; }

    public string BorderDescription { get; } = "not configured";

    public string RateLimitDescription { get; } = "not configured";

    /// <summary>Set when the source became unusable; the run should stop and report it.</summary>
    public string? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public void Start()
    {
        lock (_gate)
        {
            _running = true;
        }

        _session?.StartCapture();
    }

    /// <summary>Stops taking frames. Frames already counted stay counted; the waiting one is discarded.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            if (_pending is not null)
            {
                _discardedAtStop++;
                _pending.Dispose();
                _pending = null;
            }
        }
    }

    /// <summary>Takes the waiting frame, if any. The caller disposes it as soon as it has been converted.</summary>
    public bool TryTake(out Direct3D11CaptureFrame? frame)
    {
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
            return frame is not null;
        }
    }

    public CaptureCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new(_delivered, _skippedByPacer, _replaced, _discardedAtStop, _firstFrameTime, _lastFrameTime);
            }
        }
    }

    /// <summary>Time between consecutive delivered frames, from the capture API's own timestamps.</summary>
    public DurationSummary? DeliveryIntervals
    {
        get
        {
            lock (_gate)
            {
                return _deliveryIntervals.Summarize();
            }
        }
    }

    /// <summary>The Direct3D texture behind a captured frame. The caller releases it.</summary>
    public static ID3D11Texture2D GetTexture(Direct3D11CaptureFrame frame)
    {
        nint surface = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        object? access = null;
        try
        {
            access = Marshal.GetObjectForIUnknown(surface);
            ((IDirect3DDxgiInterfaceAccess)access).GetInterface(out ID3D11Texture2D texture);
            return texture;
        }
        finally
        {
            ComReference.Release(access);
            Marshal.Release(surface);
        }
    }

    public void Dispose()
    {
        Stop();

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _item = null;
        _device?.Dispose();
        _device = null;
    }

    private static unsafe GraphicsCaptureItem CreateItem(DisplayMonitor monitor)
    {
        // Capturing a known monitor without showing the system picker is done through this COM
        // interface on the GraphicsCaptureItem activation factory.
        IGraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        try
        {
            Guid interfaceId = GraphicsCaptureItemInterfaceId;
            interop.CreateForMonitor(new HMONITOR(monitor.Handle), &interfaceId, out void* item);
            try
            {
                return GraphicsCaptureItem.FromAbi((nint)item);
            }
            finally
            {
                Marshal.Release((nint)item);
            }
        }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Windows would not create a capture item for monitor {monitor.DeviceName} (0x{exception.HResult:X8}). "
                + "The monitor may have been disconnected or may not be capturable.",
                exception);
        }
        finally
        {
            ComReference.Release(interop);
        }
    }

    private static string ConfigureBorder(GraphicsCaptureSession session, bool requestBorderless)
    {
        if (!requestBorderless)
        {
            return "system default (Windows may draw its capture border around the monitor)";
        }

        // The supported way to ask: Windows decides, and may consult the user or policy.
        AppCapabilityAccessStatus status = GraphicsCaptureAccess
            .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (status != AppCapabilityAccessStatus.Allowed)
        {
            return $"borderless capture was requested and Windows answered {status}; the border stays";
        }

        session.IsBorderRequired = false;
        return "borderless capture was requested and Windows allowed it";
    }

    private static string ConfigureRateLimit(GraphicsCaptureSession session, TimeSpan frameInterval)
    {
        // Lets Windows skip the copies the pacer would throw away. Added in Windows 11 24H2.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            session.MinUpdateInterval = frameInterval;
            return $"GraphicsCaptureSession.MinUpdateInterval = {frameInterval.TotalMilliseconds:F3} ms, then the application pacer";
        }

        return "application pacer only (GraphicsCaptureSession.MinUpdateInterval is not available on this Windows version)";
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                _delivered++;
                TimeSpan time = frame.SystemRelativeTime;
                if (_lastFrameTime is { } previous)
                {
                    _deliveryIntervals.Add(time - previous);
                }

                _firstFrameTime ??= time;
                _lastFrameTime = time;

                SizeInt32 content = frame.ContentSize;
                if (content.Width != Size.Width || content.Height != Size.Height)
                {
                    // Following a resolution change means rebuilding the frame pool, the converter
                    // and the encoder. This experiment stops instead and says so.
                    _failure ??= $"The monitor's size changed from {Size.Width} x {Size.Height} to {content.Width} x {content.Height} during capture.";
                    _discardedAtStop++;
                    frame.Dispose();
                }
                else if (!_pacer.ShouldAccept(time))
                {
                    _skippedByPacer++;
                    frame.Dispose();
                    return;
                }
                else
                {
                    if (_pending is not null)
                    {
                        _replaced++;
                        _pending.Dispose();
                    }

                    _pending = frame;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure ??= $"Capture failed while receiving a frame: {exception.Message} (0x{exception.HResult:X8})";
            }
        }

        _wake();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_gate)
        {
            _failure ??= "The captured monitor was closed by Windows (disconnected or no longer available).";
        }

        _wake();
    }
}
