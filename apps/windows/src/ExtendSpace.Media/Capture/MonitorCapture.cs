using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtendSpace.Media.Graphics;
using ExtendSpace.Media.Interop;
using ExtendSpace.Media.Measurement;
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

namespace ExtendSpace.Media.Capture;

/// <summary>
/// How a monitor is captured: the experiment's command-line options, narrowed to what capture uses.
/// The frame rate is not among them; it is chosen after the client's capabilities are known and
/// given to <see cref="MonitorCapture.SetFrameInterval"/> before capture starts.
/// </summary>
internal sealed record MonitorCaptureSettings(bool CaptureCursor = true, bool RequestBorderless = false);

internal sealed record CaptureCounters(
    long Delivered,
    long SkippedByPacer,
    long ReplacedBeforeTaken,
    long DiscardedAtStop,
    TimeSpan? FirstFrameTime,
    TimeSpan? LastFrameTime)
{
    /// <summary>Times Windows raised the frame-arrived event, whether or not a frame was there to take.</summary>
    public long Callbacks { get; init; }

    /// <summary>Of those, the times the frame pool had no frame to give.</summary>
    public long EmptyCallbacks { get; init; }

    /// <summary>The <see cref="Stopwatch"/> timestamp of the newest delivered frame; null before the first.</summary>
    public long? LastArrival { get; init; }
}

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
    private FramePacer? _pacer;
    private readonly DurationSeries _deliveryIntervals = new();
    private readonly DurationSeries _deliveryDelays = new();
    private readonly Action _wake;

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _pending;
    private long _pendingArrival;
    private bool _running;
    private string? _failure;

    private long _callbacks;
    private long _emptyCallbacks;
    private long? _lastArrival;
    private long _delivered;
    private long _skippedByPacer;
    private long _replaced;
    private long _discardedAtStop;
    private TimeSpan? _firstFrameTime;
    private TimeSpan? _lastFrameTime;

    /// <param name="wake">Called, possibly on a capture thread, whenever there is something new to look at.</param>
    public MonitorCapture(GraphicsDevice graphics, DisplayMonitor monitor, MonitorCaptureSettings options, Action wake)
    {
        _wake = wake;

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

    public string RateLimitDescription { get; private set; } = "not configured";

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

    /// <summary>
    /// Sets the rate frames are accepted at, before <see cref="Start"/>. The experiment fixed it at
    /// construction; production learns it only once the stream has been chosen (startup step 4).
    /// </summary>
    public void SetFrameInterval(TimeSpan frameInterval)
    {
        lock (_gate)
        {
            _pacer = new FramePacer(frameInterval);
        }

        RateLimitDescription = ConfigureRateLimit(_session ?? throw new ObjectDisposedException(nameof(MonitorCapture)), frameInterval);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_pacer is null)
            {
                throw new InvalidOperationException("The frame interval has to be set before capture starts.");
            }

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
    /// <param name="arrival">
    /// The <see cref="Stopwatch"/> timestamp at which Windows handed the frame to this process.
    /// </param>
    public bool TryTake(out Direct3D11CaptureFrame? frame, out long arrival)
    {
        lock (_gate)
        {
            frame = _pending;
            arrival = _pendingArrival;
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
                return new(_delivered, _skippedByPacer, _replaced, _discardedAtStop, _firstFrameTime, _lastFrameTime)
                {
                    Callbacks = _callbacks,
                    EmptyCallbacks = _emptyCallbacks,
                    LastArrival = _lastArrival,
                };
            }
        }
    }

    /// <summary>
    /// From each frame's own timestamp to the moment it was handed to this process, both on the
    /// performance counter. It says how the timestamp relates to "now": Windows stamps a frame with
    /// a time that can be slightly ahead of its delivery, so this can be negative.
    /// </summary>
    public DurationSummary? DeliveryDelays
    {
        get
        {
            lock (_gate)
            {
                return _deliveryDelays.Summarize();
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
        // Lets Windows skip most of the copies the pacer would throw away. Added in Windows 11 24H2.
        //
        // Windows rounds this interval up to whole display refreshes, so asking for the full frame
        // interval caps the rate below the target (55 fps for 60 on a 165 Hz panel, measured). Half
        // the frame interval leaves the choosing to the pacer, which holds the average on target.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            TimeSpan minimum = frameInterval / 2;
            session.MinUpdateInterval = minimum;
            return $"GraphicsCaptureSession.MinUpdateInterval = {minimum.TotalMilliseconds:F3} ms (half a frame interval), then the application pacer";
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

                _callbacks++;
                Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    _emptyCallbacks++;
                    return;
                }

                long arrival = Stopwatch.GetTimestamp();
                _lastArrival = arrival;
                _delivered++;
                TimeSpan time = frame.SystemRelativeTime;
                _deliveryDelays.Add(Stopwatch.GetElapsedTime(0, arrival) - time);
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
                else if (!_pacer!.ShouldAccept(time))
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
                    _pendingArrival = arrival;
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
