using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Bitstream;
using ExtendSpace.Experiments.CaptureEncode.Capture;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode;

/// <summary>
/// Runs the capture pipeline once into a .h264 file. Always returns a report; a failure is recorded
/// in it, with whatever was measured before the failure, rather than thrown.
/// </summary>
internal sealed class ExperimentRunner(CaptureOptions options, TextWriter log)
{
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

    internal static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message.Trim()} (0x{exception.HResult:X8})";

    /// <summary>Copies what is known about the source and the encoder into a report.</summary>
    internal static void DescribePipeline(RunReport report, CapturePipeline pipeline)
    {
        report.Monitor = pipeline.Monitor;
        report.AdapterName = pipeline.Graphics.AdapterName;
        report.FeatureLevel = pipeline.Graphics.FeatureLevel.ToString().Replace("D3D_FEATURE_LEVEL_", string.Empty).Replace('_', '.');
        report.CaptureSize = (pipeline.Capture.Size.Width, pipeline.Capture.Size.Height);
        report.Border = pipeline.Capture.BorderDescription;
        report.RateLimit = pipeline.Capture.RateLimitDescription;
        report.Geometry = pipeline.Geometry;
        report.Encoder = pipeline.Encoder.Evidence;
        report.Acceleration = pipeline.Acceleration;
        report.EncoderSettings = pipeline.Encoder.AppliedSettings;
    }

    private void RunPipeline(RunReport report, CancellationToken cancellation)
    {
        // The writer is created after the pipeline, so that nothing is left on disk if the source
        // or the encoder cannot be opened, and closed after it, so that the encoder's last output
        // has somewhere to go.
        ElementaryStreamWriter? writer = null;
        try
        {
            using var wake = new WakeSignal();
            using CapturePipeline pipeline = CapturePipeline.Open(
                options,
                wake,
                (data, _) => writer!.WriteAccessUnit(data),
                report.RejectedEncoders);
            DescribePipeline(report, pipeline);

            writer = new ElementaryStreamWriter(options.OutputPath ?? DefaultOutputPath(DateTime.UtcNow));
            writer.SequenceHeaderProvider = pipeline.Encoder.GetSequenceHeader;

            log.WriteLine(
                $"Capturing {pipeline.Monitor.DeviceName} ({pipeline.Capture.Size.Width} x {pipeline.Capture.Size.Height}), "
                + $"encoding at {pipeline.Geometry.OutputWidth} x {pipeline.Geometry.OutputHeight}, "
                + $"for up to {options.Duration.TotalSeconds:0.###} s with '{pipeline.Encoder.Evidence.Name}' ({pipeline.Acceleration.Status}). Ctrl+C stops early.");

            CaptureAndEncode(report, pipeline, writer, wake, cancellation);
        }
        finally
        {
            if (writer is not null)
            {
                // Taken before the writer closes, so that they describe the file whatever happened above.
                report.Stream = writer.Statistics;
                report.SequenceHeaderPrepended = writer.SequenceHeaderPrepended;
                report.Frames = report.Frames with { Encoded = writer.Statistics.AccessUnits };
                if (writer.Statistics.TotalBytes > 0)
                {
                    report.OutputPath = writer.Path;
                    report.OutputBytes = writer.Statistics.TotalBytes;
                }

                writer.Dispose();
            }
        }
    }

    private void CaptureAndEncode(
        RunReport report,
        CapturePipeline pipeline,
        ElementaryStreamWriter writer,
        WakeSignal wake,
        CancellationToken cancellation)
    {
        bool encoderFailed = false;

        using TimerResolution timer = options.RaiseTimerResolution ? TimerResolution.Request() : TimerResolution.LeaveDefault();
        report.TimerResolution = timer.Description;

        using Process process = Process.GetCurrentProcess();
        TimeSpan processorTimeAtStart = process.TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        pipeline.Start();

        try
        {
            while (true)
            {
                pipeline.Pump(options.MaxFrames);

                if (pipeline.Capture.Failure is { } captureFailure)
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

                if (options.MaxFrames is { } maxFrames && pipeline.Submitted >= maxFrames)
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

                wake.Wait(remaining < CapturePipeline.PollInterval ? remaining : CapturePipeline.PollInterval);
            }
        }
        catch (Exception exception)
        {
            encoderFailed = exception is Encoding.EncoderException;
            report.StopReason = "an error occurred during capture or encoding";
            report.Errors.Add(Describe(exception));
        }

        pipeline.StopCapture();
        report.CaptureElapsed = Stopwatch.GetElapsedTime(start);

        if (!encoderFailed)
        {
            report.DrainElapsed = pipeline.Drain(wake, report.Errors);
        }

        report.ProcessorTimeWindow = Stopwatch.GetElapsedTime(start);
        process.Refresh();
        report.ProcessorTime = process.TotalProcessorTime - processorTimeAtStart;
        report.PeakWorkingSetBytes = process.PeakWorkingSet64;

        CaptureCounters counters = pipeline.Capture.Counters;
        report.Frames = new FrameCounters
        {
            Delivered = counters.Delivered,
            SkippedByPacer = counters.SkippedByPacer,
            DroppedEncoderBusy = counters.ReplacedBeforeTaken,
            DroppedNoInputSurface = pipeline.DroppedNoInputSurface,
            DiscardedAtStop = counters.DiscardedAtStop,
            Submitted = pipeline.Submitted,
            Encoded = writer.Statistics.AccessUnits,
        };
        report.CapturedTimeSpan = counters.LastFrameTime - counters.FirstFrameTime;
        report.DeliveryIntervals = pipeline.Capture.DeliveryIntervals;
        report.ConversionCalls = pipeline.ConversionCalls.Summarize();
        report.EncoderTurnaround = pipeline.Encoder.Turnaround.Summarize();
        report.UnmatchedOutputs = pipeline.Encoder.UnmatchedOutputs;
        if (writer.LastWriteTimestamp != 0)
        {
            report.ElapsedToLastOutput = Stopwatch.GetElapsedTime(start, writer.LastWriteTimestamp);
        }

        if (counters.Delivered == 0 && report.Errors.Count == 0)
        {
            report.Errors.Add(
                "The capture API delivered no frames. The display may be off, asleep or locked, or the session may not be allowed to capture it.");
        }
    }
}
