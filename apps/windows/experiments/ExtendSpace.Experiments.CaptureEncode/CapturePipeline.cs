using System.Diagnostics;
using ExtendSpace.Experiments.CaptureEncode.Capture;
using ExtendSpace.Experiments.CaptureEncode.Encoding;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using ExtendSpace.Experiments.CaptureEncode.Measurement;
using Windows.Graphics.Capture;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Experiments.CaptureEncode;

/// <summary>
/// The one capture-and-encode pipeline: monitor -> Windows.Graphics.Capture -> BGRA to NV12 (and
/// scaling) on the GPU -> H.264 encoder. What happens to the encoded access units is up to the
/// caller's handler, so writing a file and streaming live run exactly the same code up to there.
///
/// Apart from construction and disposal, every member is called from one thread, the one that
/// calls <see cref="Pump"/> in a loop.
/// </summary>
internal sealed class CapturePipeline : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private MediaFoundationSession? _mediaFoundation;
    private GraphicsDevice? _graphics;
    private MonitorCapture? _capture;
    private H264Encoder? _encoder;
    private EncoderInputPool? _inputPool;
    private Nv12Converter? _converter;
    private readonly Dictionary<long, long> _arrivals = [];
    private const int MaxRememberedArrivals = 64;

    private CapturePipeline(DisplayMonitor monitor)
    {
        Monitor = monitor;
    }

    public DisplayMonitor Monitor { get; }

    public GraphicsDevice Graphics => _graphics!;

    public MonitorCapture Capture => _capture!;

    public H264Encoder Encoder => _encoder!;

    public OutputGeometry Geometry { get; private set; } = null!;

    public EncoderSettings Settings { get; private set; } = null!;

    public AccelerationVerdict Acceleration { get; private set; } = null!;

    /// <summary>Frames handed to the encoder.</summary>
    public long Submitted { get; private set; }

    /// <summary>Frames taken from capture for which no encoder input surface was free.</summary>
    public long DroppedNoInputSurface { get; private set; }

    /// <summary>Frames taken from capture and released unencoded because the caller did not want them.</summary>
    public long SkippedNotWanted { get; private set; }

    /// <summary>Keyframe requests the encoder accepted, and those it refused.</summary>
    public long KeyframeRequestsAccepted { get; private set; }

    public long KeyframeRequestsRefused { get; private set; }

    public DurationSeries ConversionCalls { get; } = new();

    /// <summary>
    /// Set to have the pipeline remember when each submitted frame was handed over by Windows, for
    /// <see cref="TakeArrival"/>. Off by default: a caller that never asks would only let them pile up.
    /// </summary>
    public bool KeepArrivalTimes { get; set; }

    /// <summary>
    /// The capture timestamp of the first encoded frame. Sample times given to the output handler
    /// count from it, so a frame's capture time is this plus its sample time.
    /// </summary>
    public TimeSpan? FirstFrameTime { get; private set; }

    /// <summary>
    /// Builds the whole pipeline, ready to start. Encoders that were tried and could not be opened
    /// are described in <paramref name="rejectedEncoders"/>.
    /// </summary>
    public static CapturePipeline Open(
        CaptureOptions options,
        WakeSignal wake,
        EncodedSampleHandler onOutput,
        ICollection<string> rejectedEncoders)
    {
        if (!MonitorCapture.IsSupported)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture is not supported on this system or in this session.");
        }

        var pipeline = new CapturePipeline(ExperimentRunner.SelectMonitor(DisplayMonitors.Enumerate(), options.MonitorIndex));
        try
        {
            pipeline._mediaFoundation = new MediaFoundationSession();
            pipeline._graphics = GraphicsDevice.Create();
            pipeline._capture = new MonitorCapture(pipeline._graphics, pipeline.Monitor, options, wake.Set);

            var size = pipeline._capture.Size;
            pipeline.Geometry = OutputGeometry.Create(size.Width, size.Height, options.OutputSize)
                ?? throw new InvalidOperationException($"A {size.Width} x {size.Height} source is too small to encode.");
            pipeline.Settings = new EncoderSettings(
                pipeline.Geometry.OutputWidth,
                pipeline.Geometry.OutputHeight,
                options.FrameRate,
                options.BitrateBitsPerSecond,
                options.KeyframeIntervalFrames,
                options.Profile);

            pipeline._encoder = OpenEncoder(options, pipeline._graphics, pipeline.Settings, onOutput, wake.Set, rejectedEncoders);
            pipeline.Acceleration = EncoderAcceleration.Classify(pipeline._encoder.Evidence);
            pipeline._inputPool = new EncoderInputPool(pipeline._graphics, pipeline.Settings);
            pipeline._converter = new Nv12Converter(pipeline._graphics, pipeline.Geometry, options.FrameRate);
            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    public void Start() => Capture.Start();

    /// <summary>
    /// One turn of the loop: handles what the encoder has announced, then moves the waiting
    /// captured frame, if any, into the encoder.
    /// </summary>
    /// <param name="maxFrames">Stop submitting once this many frames have been submitted.</param>
    /// <param name="encode">
    /// False releases waiting frames without encoding them. Skipping a frame before it is encoded
    /// is harmless; nothing later depends on it.
    /// </param>
    public void Pump(long? maxFrames = null, bool encode = true)
    {
        Encoder.ProcessEvents();

        if (!encode)
        {
            while (Capture.TryTake(out Direct3D11CaptureFrame? unwanted, out _))
            {
                unwanted!.Dispose();
                SkippedNotWanted++;
            }

            return;
        }

        while (Encoder.CanAcceptInput
            && (maxFrames is not { } limit || Submitted < limit)
            && Capture.TryTake(out Direct3D11CaptureFrame? frame, out long arrival))
        {
            using (frame)
            {
                if (!_inputPool!.TryAllocate(out IMFSample? sample, out ID3D11Texture2D? destination, out uint subresource))
                {
                    DroppedNoInputSurface++;
                    continue;
                }

                try
                {
                    long conversionStart = Stopwatch.GetTimestamp();
                    ID3D11Texture2D source = MonitorCapture.GetTexture(frame!);
                    try
                    {
                        _converter!.Convert(source, destination!, subresource);
                    }
                    finally
                    {
                        ComReference.Release(source);
                    }

                    ConversionCalls.Add(Stopwatch.GetElapsedTime(conversionStart));

                    // Sample times start at zero and keep the capture API's own spacing.
                    TimeSpan frameTime = frame!.SystemRelativeTime;
                    FirstFrameTime ??= frameTime;
                    long sampleTime = (frameTime - FirstFrameTime.Value).Ticks;
                    if (KeepArrivalTimes)
                    {
                        RememberArrival(sampleTime, arrival);
                    }

                    Encoder.Submit(sample!, sampleTime);
                    Submitted++;
                }
                finally
                {
                    ComReference.Release(destination);
                    ComReference.Release(sample);
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="Stopwatch"/> timestamp at which Windows handed over the frame an encoded
    /// sample came from, given the sample time the output handler received. Each can be taken once.
    /// </summary>
    public long? TakeArrival(long sampleTime) => _arrivals.Remove(sampleTime, out long arrival) ? arrival : null;

    private void RememberArrival(long sampleTime, long arrival)
    {
        // The encoder returns frames in order, so anything this far behind was never going to be asked for.
        if (_arrivals.Count >= MaxRememberedArrivals)
        {
            long oldest = _arrivals.Keys.Min();
            _arrivals.Remove(oldest);
        }

        _arrivals[sampleTime] = arrival;
    }

    /// <summary>Asks the encoder to make its next picture a keyframe. Returns whether it accepted.</summary>
    public bool RequestKeyframe()
    {
        bool accepted = Encoder.RequestKeyframe();
        if (accepted)
        {
            KeyframeRequestsAccepted++;
        }
        else
        {
            KeyframeRequestsRefused++;
        }

        return accepted;
    }

    public void StopCapture() => Capture.Stop();

    /// <summary>
    /// Has the encoder emit everything it still holds. Returns how long that took; problems are
    /// added to <paramref name="errors"/>.
    /// </summary>
    public TimeSpan Drain(WakeSignal wake, ICollection<string> errors)
    {
        long drainStart = Stopwatch.GetTimestamp();
        try
        {
            Encoder.BeginDrain();
            while (!Encoder.DrainCompleted)
            {
                TimeSpan remaining = DrainTimeout - Stopwatch.GetElapsedTime(drainStart);
                if (remaining <= TimeSpan.Zero)
                {
                    errors.Add($"The encoder did not finish draining within {DrainTimeout.TotalSeconds:0} s; frames it still held were lost.");
                    break;
                }

                wake.Wait(remaining < PollInterval ? remaining : PollInterval);
                Encoder.ProcessEvents();
            }
        }
        catch (Exception exception)
        {
            errors.Add($"Draining the encoder failed. {ExperimentRunner.Describe(exception)}");
        }

        return Stopwatch.GetElapsedTime(drainStart);
    }

    /// <summary>Releases everything in the reverse of the order it was built in.</summary>
    public void Dispose()
    {
        _converter?.Dispose();
        _converter = null;
        _inputPool?.Dispose();
        _inputPool = null;
        _encoder?.Dispose();
        _encoder = null;
        _capture?.Dispose();
        _capture = null;
        _graphics?.Dispose();
        _graphics = null;
        _mediaFoundation?.Dispose();
        _mediaFoundation = null;
    }

    private static H264Encoder OpenEncoder(
        CaptureOptions options,
        GraphicsDevice graphics,
        EncoderSettings settings,
        EncodedSampleHandler onOutput,
        Action wake,
        ICollection<string> rejectedEncoders)
    {
        var candidates = new List<EncoderCandidate>();
        try
        {
            if (options.Encoder != EncoderPreference.Software)
            {
                candidates.AddRange(EncoderCatalog.FindHardware(graphics.AdapterLuid));
            }

            if (options.Encoder != EncoderPreference.Hardware)
            {
                candidates.AddRange(EncoderCatalog.FindSoftware());
            }

            if (candidates.Count == 0)
            {
                throw new EncoderException(options.Encoder == EncoderPreference.Hardware
                    ? $"Media Foundation lists no hardware H.264 encoder for the adapter '{graphics.AdapterName}'. Try '--encoder software'."
                    : "Media Foundation lists no H.264 encoder on this system.");
            }

            foreach (EncoderCandidate candidate in candidates)
            {
                try
                {
                    return H264Encoder.Open(candidate, graphics, settings, onOutput, wake);
                }
                catch (EncoderException exception)
                {
                    rejectedEncoders.Add($"{candidate.Describe()}: {exception.Message}");
                }
            }

            throw new EncoderException(
                $"None of the {candidates.Count} H.264 encoder(s) found could be opened for {settings.Width} x {settings.Height} at {settings.FrameRate} fps; each reason is listed in the report.");
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
