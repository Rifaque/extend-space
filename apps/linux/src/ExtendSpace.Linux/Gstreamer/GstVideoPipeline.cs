using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Linux.Gstreamer;

/// <summary>Turns a session's source request into a capture source, and releases it afterwards.</summary>
public interface ICaptureSourceProvider
{
    /// <summary>Resolves <paramref name="request"/>; the description is the source's size and refresh.</summary>
    Task<(CaptureSource Source, VideoSourceDescription Description)> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation);

    /// <summary>Releases what <see cref="AcquireAsync"/> made (a portal session, a virtual monitor). Safe to call twice.</summary>
    Task ReleaseAsync();
}

/// <summary>A capture failure the user can be told about.</summary>
public sealed class CaptureUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The Linux capture-and-encode pipeline (D97): GStreamer from the source (X11 or PipeWire) to an
/// Annex-B access unit, handed to the shared session exactly as the Windows pipeline does. One
/// thread pulls samples and watches the bus.
/// </summary>
public sealed class GstVideoPipeline(ICaptureSourceProvider sources, Func<string, bool>? hasElement = null, Action<string>? log = null) : IVideoPipeline, IPipelineDiagnostics
{
    private readonly Func<string, bool> _hasElement = hasElement ?? GstNative.HasElement;
    private readonly Lock _gate = new();
    private CaptureSource? _source;
    private VideoSourceDescription? _description;
    private StreamSettings? _settings;
    private nint _pipeline;
    private nint _appsink;
    private Thread? _pump;
    private volatile bool _stopping;
    private volatile bool _encoding = true;
    private volatile bool _keyframeOwed;
    private TaskCompletionSource<SessionFault?> _fault = NewFault();
    private long _captured;
    private long _encoded;
    private long _forced;
    private long _requests;
    private long _lastFrameTicks;
    private long _lastForceTicks;

    public PipelineDiagnosticCounters? DiagnosticCounters
    {
        get
        {
            StreamSettings? settings = _settings;
            if (_source is null)
            {
                return null;
            }

            long last = Interlocked.Read(ref _lastFrameTicks);
            return new PipelineDiagnosticCounters(
                Interlocked.Read(ref _captured),
                Interlocked.Read(ref _encoded),
                0,
                Interlocked.Read(ref _encoded),
                Interlocked.Read(ref _forced),
                Interlocked.Read(ref _requests),
                last == 0 ? null : Stopwatch.GetElapsedTime(last),
                settings?.FrameRate ?? 0,
                settings is null ? null : $"{settings.Width} × {settings.Height} {settings.EncoderName}");
        }
    }

    public async Task<AttemptOutcome<VideoSourceDescription>> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation)
    {
        if (GstNative.EnsureInitialised() is { } problem)
        {
            return AttemptOutcome<VideoSourceDescription>.Failed(new SessionFault(FaultCategory.Capture, "gstreamer_missing", problem, FaultTreatment.Terminal, UserActionable: true));
        }

        try
        {
            (CaptureSource source, VideoSourceDescription description) = await sources.AcquireAsync(request, cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                _source = source;
                _description = description;
                _fault = NewFault();
            }

            log?.Invoke($"capture: acquired {source} {description.Width} x {description.Height}");
            return AttemptOutcome<VideoSourceDescription>.Succeeded(description);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return AttemptOutcome<VideoSourceDescription>.Cancelled;
        }
        catch (CaptureUnavailableException e)
        {
            return AttemptOutcome<VideoSourceDescription>.Failed(new SessionFault(FaultCategory.Capture, e.Code, e.Message, FaultTreatment.Terminal, UserActionable: true));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return AttemptOutcome<VideoSourceDescription>.Failed(new SessionFault(FaultCategory.Capture, "capture_unavailable", $"The display could not be acquired: {e.Message}", FaultTreatment.Terminal));
        }
    }

    public Task<AttemptOutcome<StreamSettings>> PrepareAsync(EncodingRequest request, CancellationToken cancellation)
    {
        CaptureSource? source = _source;
        if (source is null)
        {
            return Task.FromResult(AttemptOutcome<StreamSettings>.Failed(new SessionFault(FaultCategory.Encoder, "not_acquired", "No display was acquired.", FaultTreatment.Terminal)));
        }

        foreach (bool hardware in new[] { true, false })
        {
            EncoderChoice? encoder = PipelineDescription.ChooseEncoder(_hasElement, hardware);
            if (encoder is null)
            {
                continue;
            }

            string description = PipelineDescription.Build(source, request.Width, request.Height, request.FrameRate, request.BitrateBitsPerSecond, encoder, parser: _hasElement("h264parse"));
            nint pipeline = GstNative.ParseLaunch(description, out nint error);
            if (pipeline == 0 || error != 0)
            {
                log?.Invoke($"encoder: {encoder.Element} unusable: {GstNative.ErrorMessage(error)}");
                if (error != 0)
                {
                    GstNative.ErrorFree(error);
                }

                if (pipeline != 0)
                {
                    GstNative.ObjectUnref(pipeline);
                }

                continue;
            }

            lock (_gate)
            {
                _pipeline = pipeline;
                _appsink = GstNative.BinGetByName(pipeline, PipelineDescription.SinkName);
                _settings = new StreamSettings(request.Width, request.Height, request.FrameRate, request.BitrateBitsPerSecond, encoder.Name, encoder.IsHardware);
            }

            log?.Invoke($"encoder: {encoder.Name} {request.Width} x {request.Height} at {request.FrameRate} fps, {request.BitrateBitsPerSecond / 1000} kbit/s");
            return Task.FromResult(AttemptOutcome<StreamSettings>.Succeeded(_settings!));
        }

        return Task.FromResult(AttemptOutcome<StreamSettings>.Failed(new SessionFault(
            FaultCategory.Encoder,
            "no_h264_encoder",
            "No H.264 encoder was found. Install the GStreamer x264 plugin (gstreamer1.0-plugins-ugly on Debian and Ubuntu, gstreamer1-plugins-ugly on Fedora).",
            FaultTreatment.Terminal,
            UserActionable: true)));
    }

    public void Start(EncodedVideoSink sink)
    {
        lock (_gate)
        {
            if (_pipeline == 0 || _pump is not null)
            {
                return;
            }

            _stopping = false;
            if (GstNative.SetState(_pipeline, GstNative.StatePlaying) == GstNative.StateChangeFailure)
            {
                Fail("capture_failed", "GStreamer could not start capturing.");
                return;
            }

            _pump = new Thread(() => Pump(sink)) { IsBackground = true, Name = "ExtendSpace capture" };
            _pump.Start();
        }
    }

    public void SetEncoding(bool enabled) => _encoding = enabled;

    public void RequestKeyframe()
    {
        if (_source is null)
        {
            return;
        }

        Interlocked.Increment(ref _requests);
        _keyframeOwed = true;
        nint sink = _appsink;
        if (sink != 0 && _encoding)
        {
            Interlocked.Exchange(ref _lastForceTicks, Stopwatch.GetTimestamp());
            GstNative.SendEvent(sink, GstNative.NewUpstreamForceKeyUnit(GstNative.ClockTimeNone, allHeaders: true, 0));
        }
    }

    public async Task<SessionFault?> WaitForFaultAsync(CancellationToken cancellation) => await _fault.Task.WaitAsync(cancellation).ConfigureAwait(false);

    public async Task StopAsync(CancellationToken cancellation)
    {
        Thread? pump;
        nint pipeline;
        nint appsink;
        lock (_gate)
        {
            _stopping = true;
            pump = _pump;
            pipeline = _pipeline;
            appsink = _appsink;
            _pump = null;
            _pipeline = 0;
            _appsink = 0;
        }

        pump?.Join(TimeSpan.FromSeconds(3));
        if (pipeline != 0)
        {
            GstNative.SetState(pipeline, GstNative.StateNull);
            if (appsink != 0)
            {
                GstNative.ObjectUnref(appsink);
            }

            GstNative.ObjectUnref(pipeline);
        }

        await sources.ReleaseAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _source = null;
            _description = null;
            _settings = null;
            _fault.TrySetResult(null);
        }
    }

    private void Pump(EncodedVideoSink sink)
    {
        nint pipeline = _pipeline;
        nint appsink = _appsink;
        nint bus = GstNative.GetBus(pipeline);
        try
        {
            while (!_stopping)
            {
                nint message = GstNative.BusPop(bus, 0, GstNative.MessageError | GstNative.MessageEos);
                if (message != 0)
                {
                    GstNative.MessageParseError(message, out nint error, out nint debug);
                    string text = error == 0 ? "the capture stream ended" : GstNative.ErrorMessage(error);
                    if (error != 0)
                    {
                        GstNative.ErrorFree(error);
                    }

                    if (debug != 0)
                    {
                        GstNative.Free(debug);
                    }

                    GstNative.MiniObjectUnref(message);
                    Fail("capture_failed", $"Capture stopped: {text}");
                    return;
                }

                nint sample = GstNative.AppSinkTryPull(appsink, 100_000_000);
                if (sample == 0)
                {
                    continue;
                }

                try
                {
                    Deliver(sink, sample);
                }
                finally
                {
                    GstNative.MiniObjectUnref(sample);
                }
            }
        }
        finally
        {
            GstNative.ObjectUnref(bus);
        }
    }

    private unsafe void Deliver(EncodedVideoSink sink, nint sample)
    {
        Interlocked.Increment(ref _captured);
        Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
        if (!_encoding)
        {
            return;
        }

        nint buffer = GstNative.SampleGetBuffer(sample);
        if (buffer == 0 || !GstNative.BufferMap(buffer, out GstNative.MapInfo map, GstNative.MapRead))
        {
            return;
        }

        try
        {
            var accessUnit = new ReadOnlySpan<byte>(map.Data, checked((int)map.Size));
            bool keyframe = AnnexB.IsKeyframe(accessUnit);
            if (keyframe && _keyframeOwed)
            {
                _keyframeOwed = false;
                Interlocked.Increment(ref _forced);
            }
            else if (_keyframeOwed && !keyframe && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastForceTicks)) > TimeSpan.FromMilliseconds(500))
            {
                Interlocked.Exchange(ref _lastForceTicks, Stopwatch.GetTimestamp());
                // The request reached the encoder before encoding resumed: ask again.
                GstNative.SendEvent(_appsink, GstNative.NewUpstreamForceKeyUnit(GstNative.ClockTimeNone, allHeaders: true, 0));
            }

            Interlocked.Increment(ref _encoded);
            // The connection's clock (Stopwatch, as ConnectionTiming's MonotonicClock); the frame was
            // captured at most one frame interval earlier.
            sink(accessUnit, (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency));
        }
        finally
        {
            GstNative.BufferUnmap(buffer, ref map);
        }
    }

    private void Fail(string code, string message)
    {
        log?.Invoke($"capture: {message}");
        _fault.TrySetResult(new SessionFault(FaultCategory.Capture, code, message, FaultTreatment.Terminal));
    }

    private static TaskCompletionSource<SessionFault?> NewFault() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
