using System.Diagnostics.CodeAnalysis;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Media.Capture;
using ExtendSpace.Media.Encoding;
using ExtendSpace.Media.Graphics;
using ExtendSpace.Media.Interop;
using Windows.Graphics.Capture;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Media;

/// <summary>The real backend: Windows.Graphics.Capture, the Direct3D 11 video processor, Media Foundation.</summary>
internal sealed class DesktopMediaBackend : IMediaBackend
{
    public ICaptureSource OpenSource(VideoSourceRequest request, Action wake) => DesktopCaptureSource.Open(request, wake);
}

/// <summary>
/// One monitor with its Direct3D device and capture session, as the experiment's
/// <c>CapturePipeline.Open</c> built them, minus the encoder, which is opened later for the stream
/// the session chooses.
/// </summary>
internal sealed class DesktopCaptureSource : ICaptureSource
{
    private MediaFoundationSession? _mediaFoundation;
    private GraphicsDevice? _graphics;
    private MonitorCapture? _capture;
    private CaptureActivity _finalActivity = CaptureActivity.None;

    private DesktopCaptureSource(DisplayMonitor monitor)
    {
        Monitor = monitor;
    }

    public DisplayMonitor Monitor { get; }

    public VideoSourceDescription Description { get; private set; } = null!;

    public IReadOnlyList<string> Details { get; private set; } = [];

    public string? Failure => _capture?.Failure;

    public CaptureActivity Activity
    {
        get
        {
            MonitorCapture? capture = _capture;
            if (capture is null)
            {
                return _finalActivity;
            }

            CaptureCounters counters = capture.Counters;
            return new CaptureActivity(counters.Callbacks, counters.EmptyCallbacks, counters.Delivered, counters.SkippedByPacer, counters.ReplacedBeforeTaken, counters.LastArrival);
        }
    }

    public static DesktopCaptureSource Open(VideoSourceRequest request, Action wake)
    {
        if (!MonitorCapture.IsSupported)
        {
            throw new MediaSourceException(MediaFaultCodes.CaptureUnsupported, "Windows.Graphics.Capture is not supported on this system or in this session.");
        }

        IReadOnlyList<DisplayMonitor> monitors = DisplayMonitors.Enumerate();
        DisplayMonitor monitor = SelectMonitor(monitors, request.MonitorIndex, request.MonitorDeviceName);
        var source = new DesktopCaptureSource(monitor);
        try
        {
            source._mediaFoundation = new MediaFoundationSession();
            source._graphics = GraphicsDevice.Create();
            source._capture = new MonitorCapture(source._graphics, monitor, new MonitorCaptureSettings(), wake);

            int? refresh = DisplayMonitors.RefreshRate(monitor);
            source.Description = new VideoSourceDescription(source._capture.Size.Width, source._capture.Size.Height, refresh ?? 0);
            source.Details =
            [
                $"monitor: {monitor.DeviceName}{(monitor.IsPrimary ? " (primary)" : string.Empty)}, capture size {source._capture.Size.Width} x {source._capture.Size.Height}, {(refresh is { } hz ? $"{hz} Hz" : "refresh rate unknown")}",
                $"adapter: {source._graphics.AdapterName}, feature level {source._graphics.FeatureLevel.ToString().Replace("D3D_FEATURE_LEVEL_", string.Empty).Replace('_', '.')}",
                $"capture border: {source._capture.BorderDescription}",
            ];
            return source;
        }
        catch (Exception exception)
        {
            source.Dispose();
            throw exception as MediaSourceException ?? new MediaSourceException(
                MediaFaultCodes.CaptureUnavailable,
                $"Monitor {monitor.DeviceName} could not be prepared for capture: {DesktopVideoPipeline.Describe(exception)}",
                exception);
        }
    }

    internal static DisplayMonitor SelectMonitor(IReadOnlyList<DisplayMonitor> monitors, int? requestedIndex, string? requestedDeviceName = null)
    {
        if (monitors.Count == 0)
        {
            throw new MediaSourceException(MediaFaultCodes.MonitorNotFound, "Windows reports no monitors attached to the desktop, so there is nothing to capture.");
        }

        // The display the user chose, by name: if it has gone, the session fails rather than
        // mirroring another one (D68).
        if (requestedDeviceName is not null)
        {
            return monitors.FirstOrDefault(monitor => string.Equals(monitor.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new MediaSourceException(MediaFaultCodes.MonitorNotFound, $"The display you chose ({DisplayCatalog.Label(requestedDeviceName)}) is no longer connected. Choose another display.");
        }

        if (requestedIndex is { } index)
        {
            return index >= 0 && index < monitors.Count
                ? monitors[index]
                : throw new MediaSourceException(MediaFaultCodes.MonitorNotFound, $"There is no monitor {index}; Windows reports {monitors.Count}, numbered from 0.");
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    public IFrameEncoder OpenEncoder(EncodingRequest request, EncodedOutputHandler onOutput, Action wake)
    {
        MonitorCapture capture = _capture ?? throw new ObjectDisposedException(nameof(DesktopCaptureSource));
        if (request.FrameRate <= 0 || request.Width <= 0 || request.Height <= 0 || request.BitrateBitsPerSecond <= 0)
        {
            throw new MediaEncoderException(MediaFaultCodes.EncoderUnavailable, $"{request.Width} x {request.Height} at {request.FrameRate} fps and {request.BitrateBitsPerSecond} bit/s is not a stream that can be encoded.");
        }

        OutputGeometry geometry = OutputGeometry.Create(capture.Size.Width, capture.Size.Height, (request.Width, request.Height))
            ?? throw new MediaEncoderException(MediaFaultCodes.SourceTooSmall, $"A {capture.Size.Width} x {capture.Size.Height} source is too small to encode.");

        // As the experiment: a keyframe every two seconds besides the ones asked for.
        var settings = new EncoderSettings(geometry.OutputWidth, geometry.OutputHeight, request.FrameRate, request.BitrateBitsPerSecond, Math.Max(1, request.FrameRate * 2), H264Profile.High);
        MediaFoundationFrameEncoder encoder = MediaFoundationFrameEncoder.Open(_graphics!, geometry, settings, onOutput, wake);
        try
        {
            capture.SetFrameInterval(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / request.FrameRate));
            encoder.AddDetail($"capture rate: {capture.RateLimitDescription}");
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    public void Start() => _capture?.Start();

    public void Stop() => _capture?.Stop();

    public bool TryTake([NotNullWhen(true)] out ICapturedFrame? frame)
    {
        frame = null;
        if (_capture is not { } capture || !capture.TryTake(out Direct3D11CaptureFrame? captured, out long arrival))
        {
            return false;
        }

        frame = new CapturedFrame(captured!, arrival);
        return true;
    }

    public void Dispose()
    {
        if (_capture is { } capture)
        {
            _finalActivity = Activity;
            capture.Dispose();
            _capture = null;
        }

        _graphics?.Dispose();
        _graphics = null;
        _mediaFoundation?.Dispose();
        _mediaFoundation = null;
    }

    /// <summary>A captured frame, returned to the capture pool when disposed.</summary>
    internal sealed class CapturedFrame(Direct3D11CaptureFrame frame, long arrival) : ICapturedFrame
    {
        private Direct3D11CaptureFrame? _frame = frame;

        public long Arrival { get; } = arrival;

        public Direct3D11CaptureFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(CapturedFrame));

        public void Dispose()
        {
            _frame?.Dispose();
            _frame = null;
        }
    }
}

/// <summary>
/// The experiment's conversion and encoder, as <c>CapturePipeline</c> drove them: a pooled NV12
/// surface per frame, the video processor's BGRA to NV12 pass into it, then the H.264 transform.
/// Hardware transforms on the capture adapter are tried first, then the software one.
/// </summary>
internal sealed class MediaFoundationFrameEncoder : IFrameEncoder
{
    private readonly List<string> _details = [];
    private H264Encoder? _encoder;
    private EncoderInputPool? _inputPool;
    private Nv12Converter? _converter;
    private EncodedOutputCheck? _check;

    private MediaFoundationFrameEncoder(StreamSettings settings)
    {
        Settings = settings;
    }

    public StreamSettings Settings { get; }

    public IReadOnlyList<string> Details => _details;

    public bool CanAcceptInput => _encoder?.CanAcceptInput ?? false;

    public string? ProducedStream => _check?.First?.ToString();

    public static MediaFoundationFrameEncoder Open(GraphicsDevice graphics, OutputGeometry geometry, EncoderSettings settings, EncodedOutputHandler onOutput, Action wake)
    {
        var rejected = new List<string>();
        var check = new EncodedOutputCheck(settings.Width, settings.Height, settings.Profile);
        H264Encoder encoder = OpenFirstUsable(
            graphics,
            settings,
            (data, sampleTime) =>
            {
                // Nothing reaches the client that is not the stream it was configured for (D57).
                check.Verify(data);
                onOutput(data, sampleTime);
            },
            wake,
            rejected);
        AccelerationVerdict acceleration = EncoderAcceleration.Classify(encoder.Evidence);
        var opened = new MediaFoundationFrameEncoder(new StreamSettings(
            settings.Width,
            settings.Height,
            settings.FrameRate,
            settings.BitrateBitsPerSecond,
            encoder.Evidence.Name,
            acceleration.Status == AccelerationStatus.Hardware))
        {
            _encoder = encoder,
            _check = check,
        };

        try
        {
            opened._inputPool = new EncoderInputPool(graphics, settings);
            opened._converter = new Nv12Converter(graphics, geometry, settings.FrameRate);
        }
        catch (Exception exception)
        {
            opened.Dispose();
            throw new MediaEncoderException(MediaFoundationFaults.ConversionUnavailable, $"The GPU conversion in front of the encoder could not be set up: {DesktopVideoPipeline.Describe(exception)}", exception);
        }

        opened._details.Add($"encoder: {encoder.Evidence.Name}, {(encoder.IsAsync ? "asynchronous" : "synchronous")}, acceleration {acceleration.Status}: {acceleration.Reason}");
        opened._details.Add($"geometry: source {geometry.SourceWidth} x {geometry.SourceHeight} -> picture {geometry.PictureWidth} x {geometry.PictureHeight} at ({geometry.PictureX}, {geometry.PictureY}) in {geometry.OutputWidth} x {geometry.OutputHeight}");
        opened._details.AddRange(encoder.AppliedSettings.Select(applied => $"encoder setting: {applied}"));
        opened._details.AddRange(rejected.Select(reason => $"encoder not used: {reason}"));
        return opened;
    }

    public void AddDetail(string detail) => _details.Add(detail);

    public void ProcessEvents()
    {
        try
        {
            _encoder!.ProcessEvents();
        }
        catch (EncoderException exception)
        {
            throw new MediaEncoderException(MediaFaultCodes.EncoderFailed, exception.Message, exception);
        }
    }

    public bool ForceKeyframe() => _encoder?.RequestKeyframe() ?? false;

    public bool Submit(ICapturedFrame frame, long sampleTime)
    {
        var captured = (DesktopCaptureSource.CapturedFrame)frame;
        if (!_inputPool!.TryAllocate(out IMFSample? sample, out ID3D11Texture2D? destination, out uint subresource))
        {
            return false;
        }

        try
        {
            ID3D11Texture2D source = MonitorCapture.GetTexture(captured.Frame);
            try
            {
                _converter!.Convert(source, destination!, subresource);
            }
            finally
            {
                ComReference.Release(source);
            }

            _encoder!.Submit(sample!, sampleTime);
            return true;
        }
        catch (EncoderException exception)
        {
            throw new MediaEncoderException(MediaFaultCodes.EncoderFailed, exception.Message, exception);
        }
        finally
        {
            ComReference.Release(destination);
            ComReference.Release(sample);
        }
    }

    public void Dispose()
    {
        _converter?.Dispose();
        _converter = null;
        _inputPool?.Dispose();
        _inputPool = null;
        _encoder?.Dispose();
        _encoder = null;
    }

    private static H264Encoder OpenFirstUsable(GraphicsDevice graphics, EncoderSettings settings, EncodedSampleHandler onOutput, Action wake, List<string> rejected)
    {
        var candidates = new List<EncoderCandidate>();
        try
        {
            candidates.AddRange(EncoderCatalog.FindHardware(graphics.AdapterLuid));
            candidates.AddRange(EncoderCatalog.FindSoftware());
            if (candidates.Count == 0)
            {
                throw new MediaEncoderException(MediaFaultCodes.EncoderUnavailable, "Media Foundation lists no H.264 encoder on this system.");
            }

            foreach (EncoderCandidate candidate in candidates)
            {
                try
                {
                    return H264Encoder.Open(candidate, graphics, settings, onOutput, wake);
                }
                catch (EncoderException exception)
                {
                    rejected.Add($"{candidate.Describe()}: {exception.Message}");
                }
            }

            throw new MediaEncoderException(
                MediaFaultCodes.EncoderUnavailable,
                $"None of the {candidates.Count} H.264 encoder(s) found could be opened for {settings.Width} x {settings.Height} at {settings.FrameRate} fps: {string.Join(" | ", rejected)}");
        }
        catch (Exception exception) when (exception is not MediaEncoderException)
        {
            throw new MediaEncoderException(MediaFaultCodes.EncoderUnavailable, $"The H.264 encoders could not be listed: {DesktopVideoPipeline.Describe(exception)}", exception);
        }
        finally
        {
            foreach (EncoderCandidate candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }
}

/// <summary>Codes only the Media Foundation backend raises.</summary>
internal static class MediaFoundationFaults
{
    public const string ConversionUnavailable = "conversion_unavailable";
}
