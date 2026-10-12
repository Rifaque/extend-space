using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Bitstream;
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
/// Runs the pipeline once: monitor -> Windows.Graphics.Capture -> BGRA to NV12 on the GPU ->
/// H.264 encoder -> .h264 file. Always returns a report; a failure is recorded in it, with
/// whatever was measured before the failure, rather than thrown.
/// </summary>
internal sealed class ExperimentRunner(CaptureOptions options, TextWriter log)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public RunReport Run(CancellationToken cancellation)
    {
        var report = new RunReport(options);
        try
        {
            RunPipeline(report, cancellation);
        }
        catch (Exception exception)
        {
            report.Errors.Add(Describe(exception));
        }

        return report;
    }

    internal static string DefaultOutputPath(DateTime utcNow) =>
        $"extendspace-capture-{utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.h264";

    internal static DisplayMonitor SelectMonitor(IReadOnlyList<DisplayMonitor> monitors, int? requestedIndex)
    {
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows reports no monitors attached to the desktop, so there is nothing to capture.");
        }

        if (requestedIndex is { } index)
        {
            return index < monitors.Count
                ? monitors[index]
                : throw new InvalidOperationException(
                    $"There is no monitor {index}. Windows reports {monitors.Count} monitor(s), numbered from 0; run 'list' to see them.");
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message.Trim()} (0x{exception.HResult:X8})";

    private void RunPipeline(RunReport report, CancellationToken cancellation)
    {
        if (!MonitorCapture.IsSupported)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture is not supported on this system or in this session.");
        }

        DisplayMonitor monitor = SelectMonitor(DisplayMonitors.Enumerate(), options.MonitorIndex);
        report.Monitor = monitor;

        using var mediaFoundation = new MediaFoundationSession();
        using var graphics = GraphicsDevice.Create();
        report.AdapterName = graphics.AdapterName;
        report.FeatureLevel = graphics.FeatureLevel.ToString().Replace("D3D_FEATURE_LEVEL_", string.Empty).Replace('_', '.');

        using var wake = new WakeSignal();
        using var capture = new MonitorCapture(graphics, monitor, options, wake.Set);
        report.CaptureSize = (capture.Size.Width, capture.Size.Height);
        report.Border = capture.BorderDescription;
        report.RateLimit = capture.RateLimitDescription;

        OutputGeometry geometry = OutputGeometry.Create(capture.Size.Width, capture.Size.Height, options.OutputSize)
            ?? throw new InvalidOperationException($"A {capture.Size.Width} x {capture.Size.Height} source is too small to encode.");
        report.Geometry = geometry;
        int width = geometry.OutputWidth;
        int height = geometry.OutputHeight;

        var settings = new EncoderSettings(
            width,
            height,
            options.FrameRate,
            options.BitrateBitsPerSecond,
            options.KeyframeIntervalFrames,
            options.Profile);

        using var writer = new ElementaryStreamWriter(options.OutputPath ?? DefaultOutputPath(DateTime.UtcNow));
        try
        {
            using H264Encoder encoder = OpenEncoder(graphics, settings, (data, _) => writer.WriteAccessUnit(data), wake.Set, report);
            writer.SequenceHeaderProvider = encoder.GetSequenceHeader;
            report.Encoder = encoder.Evidence;
            report.Acceleration = EncoderAcceleration.Classify(encoder.Evidence);
            report.EncoderSettings = encoder.AppliedSettings;

            using var inputPool = new EncoderInputPool(graphics, settings);
            using var converter = new Nv12Converter(graphics, geometry, options.FrameRate);

            log.WriteLine(
                $"Capturing {monitor.DeviceName} ({capture.Size.Width} x {capture.Size.Height}), encoding at {width} x {height}, "
                + $"for up to {options.Duration.TotalSeconds:0.###} s with '{encoder.Evidence.Name}' ({report.Acceleration.Status}). Ctrl+C stops early.");

            CaptureAndEncode(report, capture, encoder, inputPool, converter, writer, wake, cancellation);
        }
        finally
        {
            // Taken here, before the writer closes, so that they describe the file whatever happened above.
            report.Stream = writer.Statistics;
            report.SequenceHeaderPrepended = writer.SequenceHeaderPrepended;
            report.Frames = report.Frames with { Encoded = writer.Statistics.AccessUnits };
            if (writer.Statistics.TotalBytes > 0)
            {
                report.OutputPath = writer.Path;
                report.OutputBytes = writer.Statistics.TotalBytes;
            }
        }
    }

    private void CaptureAndEncode(
        RunReport report,
        MonitorCapture capture,
        H264Encoder encoder,
        EncoderInputPool inputPool,
        Nv12Converter converter,
        ElementaryStreamWriter writer,
        WakeSignal wake,
        CancellationToken cancellation)
    {
        var conversionCalls = new DurationSeries();
        long submitted = 0;
        long droppedNoInputSurface = 0;
        TimeSpan? firstFrameTime = null;
        bool encoderFailed = false;

        using TimerResolution timer = options.RaiseTimerResolution ? TimerResolution.Request() : TimerResolution.LeaveDefault();
        report.TimerResolution = timer.Description;

        using Process process = Process.GetCurrentProcess();
        TimeSpan processorTimeAtStart = process.TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        capture.Start();

        try
        {
            while (true)
            {
                encoder.ProcessEvents();
                SubmitWaitingFrames();

                if (capture.Failure is { } captureFailure)
                {
                    report.StopReason = "the capture source failed";
                    report.Errors.Add(captureFailure);
                    break;
                }

                if (cancellation.IsCancellationRequested)
                {
                    report.StopReason = "cancelled by the user (Ctrl+C or Ctrl+Break)";
                    break;
                }

                if (options.MaxFrames is { } maxFrames && submitted >= maxFrames)
                {
                    report.StopReason = $"the frame limit of {maxFrames} was reached";
                    break;
                }

                TimeSpan remaining = options.Duration - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    report.StopReason = "the requested duration elapsed";
                    break;
                }

                wake.Wait(remaining < PollInterval ? remaining : PollInterval);
            }
        }
        catch (Exception exception)
        {
            encoderFailed = exception is EncoderException;
            report.StopReason = "an error occurred during capture or encoding";
            report.Errors.Add(Describe(exception));
        }

        capture.Stop();
        report.CaptureElapsed = Stopwatch.GetElapsedTime(start);

        if (!encoderFailed)
        {
            Drain();
        }

        report.ProcessorTimeWindow = Stopwatch.GetElapsedTime(start);
        process.Refresh();
        report.ProcessorTime = process.TotalProcessorTime - processorTimeAtStart;
        report.PeakWorkingSetBytes = process.PeakWorkingSet64;

        CaptureCounters counters = capture.Counters;
        report.Frames = new FrameCounters
        {
            Delivered = counters.Delivered,
            SkippedByPacer = counters.SkippedByPacer,
            DroppedEncoderBusy = counters.ReplacedBeforeTaken,
            DroppedNoInputSurface = droppedNoInputSurface,
            DiscardedAtStop = counters.DiscardedAtStop,
            Submitted = submitted,
            Encoded = writer.Statistics.AccessUnits,
        };
        report.CapturedTimeSpan = counters.LastFrameTime - counters.FirstFrameTime;
        report.DeliveryIntervals = capture.DeliveryIntervals;
        report.ConversionCalls = conversionCalls.Summarize();
        report.EncoderTurnaround = encoder.Turnaround.Summarize();
        report.UnmatchedOutputs = encoder.UnmatchedOutputs;
        if (writer.LastWriteTimestamp != 0)
        {
            report.ElapsedToLastOutput = Stopwatch.GetElapsedTime(start, writer.LastWriteTimestamp);
        }

        if (counters.Delivered == 0 && report.Errors.Count == 0)
        {
            report.Errors.Add(
                "The capture API delivered no frames. The display may be off, asleep or locked, or the session may not be allowed to capture it.");
        }

        void SubmitWaitingFrames()
        {
            while (encoder.CanAcceptInput
                && (options.MaxFrames is not { } maxFrames || submitted < maxFrames)
                && capture.TryTake(out Direct3D11CaptureFrame? frame))
            {
                using (frame)
                {
                    if (!inputPool.TryAllocate(out IMFSample? sample, out ID3D11Texture2D? destination, out uint subresource))
                    {
                        droppedNoInputSurface++;
                        continue;
                    }

                    try
                    {
                        long conversionStart = Stopwatch.GetTimestamp();
                        ID3D11Texture2D source = MonitorCapture.GetTexture(frame!);
                        try
                        {
                            converter.Convert(source, destination!, subresource);
                        }
                        finally
                        {
                            ComReference.Release(source);
                        }

                        conversionCalls.Add(Stopwatch.GetElapsedTime(conversionStart));

                        // Sample times start at zero and keep the capture API's own spacing.
                        TimeSpan frameTime = frame!.SystemRelativeTime;
                        firstFrameTime ??= frameTime;
                        encoder.Submit(sample!, (frameTime - firstFrameTime.Value).Ticks);
                        submitted++;
                    }
                    finally
                    {
                        ComReference.Release(destination);
                        ComReference.Release(sample);
                    }
                }
            }
        }

        void Drain()
        {
            long drainStart = Stopwatch.GetTimestamp();
            try
            {
                encoder.BeginDrain();
                while (!encoder.DrainCompleted)
                {
                    TimeSpan remaining = DrainTimeout - Stopwatch.GetElapsedTime(drainStart);
                    if (remaining <= TimeSpan.Zero)
                    {
                        report.Errors.Add(
                            $"The encoder did not finish draining within {DrainTimeout.TotalSeconds:0} s; frames it still held are missing from the file.");
                        break;
                    }

                    wake.Wait(remaining < PollInterval ? remaining : PollInterval);
                    encoder.ProcessEvents();
                }
            }
            catch (Exception exception)
            {
                report.Errors.Add($"Draining the encoder failed. {Describe(exception)}");
            }

            report.DrainElapsed = Stopwatch.GetElapsedTime(drainStart);
        }
    }

    private H264Encoder OpenEncoder(
        GraphicsDevice graphics,
        EncoderSettings settings,
        EncodedSampleHandler onOutput,
        Action wake,
        RunReport report)
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
                    report.RejectedEncoders.Add($"{candidate.Describe()}: {exception.Message}");
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
