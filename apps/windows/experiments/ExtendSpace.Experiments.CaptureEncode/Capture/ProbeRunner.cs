using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Capture;

/// <summary>One second of what the capture API and the encoder did.</summary>
internal readonly record struct ProbeSecond(
    double Seconds,
    long Callbacks,
    long EmptyCallbacks,
    long Delivered,
    long SkippedByPacer,
    long Replaced,
    long Submitted,
    long Encoded,
    long EncodedBytes,
    double? NewestFrameAgeMs)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Seconds,6:0.0} s] capture: callbacks {Callbacks,4} empty {EmptyCallbacks,3} frames {Delivered,4} paced-out {SkippedByPacer,4} overtaken {Replaced,3}"
        + $" | encoder: in {Submitted,4} out {Encoded,4} {EncodedBytes * 8 / 1000.0,8:0} kbit"
        + $" | newest frame {(NewestFrameAgeMs is { } age ? $"{age,7:0} ms old" : "none yet")}");
}

/// <summary>
/// Runs the same capture and encoding as a stream, sends nothing and keeps nothing, and prints
/// once a second what the capture API delivered. It exists to answer one question: when the
/// picture on the other device stops moving, has Windows stopped delivering frames, or is this
/// program throwing them away?
///
/// "callbacks" counts every frame-arrived event Windows raised, before any of this program's own
/// decisions; "paced-out" and "overtaken" are frames this program chose not to encode.
/// </summary>
internal sealed class ProbeRunner(CaptureOptions options, bool encode, TextWriter log)
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <returns>True if the probe ran to its end without the capture source or the encoder failing.</returns>
    public bool Run(CancellationToken cancellation)
    {
        var errors = new List<string>();
        var rejected = new List<string>();
        var seconds = new List<ProbeSecond>();
        string stopped = "the requested duration elapsed";
        long encoded = 0;
        long encodedBytes = 0;

        try
        {
            using var wake = new WakeSignal();
            using CapturePipeline pipeline = CapturePipeline.Open(
                options,
                wake,
                (data, _) =>
                {
                    // The encoded picture is counted and dropped. Nothing is written anywhere.
                    encoded++;
                    encodedBytes += data.Length;
                },
                rejected);

            string encoding = encode
                ? $"encoding at {pipeline.Geometry.OutputWidth} x {pipeline.Geometry.OutputHeight} with '{pipeline.Encoder.Evidence.Name}' and discarding the result."
                : "not encoding (--no-encode): frames are released as soon as they are counted.";
            log.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Probing {pipeline.Monitor.DeviceName} (monitor {pipeline.Monitor.Index}, {pipeline.Capture.Size.Width} x {pipeline.Capture.Size.Height}) on '{pipeline.Graphics.AdapterName}' for {options.Duration.TotalSeconds:0.#} s at a target of {options.FrameRate} fps; {encoding}"));
            log.WriteLine($"Frame-rate limit: {pipeline.Capture.RateLimitDescription}");
            log.WriteLine("Nothing is recorded or sent.");

            using TimerResolution timer = options.RaiseTimerResolution ? TimerResolution.Request() : TimerResolution.LeaveDefault();
            long start = Stopwatch.GetTimestamp();
            long nextSample = start + Stopwatch.Frequency;
            var previous = Snapshot.Take(pipeline, encoded, encodedBytes);
            pipeline.Start();

            while (true)
            {
                pipeline.Pump(encode: encode);
                long now = Stopwatch.GetTimestamp();
                if (now >= nextSample)
                {
                    var current = Snapshot.Take(pipeline, encoded, encodedBytes);
                    ProbeSecond second = current.Since(previous, Stopwatch.GetElapsedTime(start, now).TotalSeconds, now);
                    seconds.Add(second);
                    log.WriteLine(second.Describe());
                    previous = current;
                    nextSample += (long)(SampleInterval.TotalSeconds * Stopwatch.Frequency);
                }

                if (pipeline.Capture.Failure is { } failure)
                {
                    stopped = "the capture source failed";
                    errors.Add(failure);
                    break;
                }

                if (cancellation.IsCancellationRequested)
                {
                    stopped = "cancelled (Ctrl+C or Ctrl+Break)";
                    break;
                }

                if (Stopwatch.GetElapsedTime(start, now) >= options.Duration)
                {
                    break;
                }

                wake.Wait(CapturePipeline.PollInterval);
            }

            pipeline.StopCapture();
            pipeline.Drain(wake, errors);
            WriteSummary(pipeline.Capture.Counters, pipeline, seconds, stopped, encoded, encodedBytes, rejected, errors);
        }
        catch (Exception exception)
        {
            errors.Add(ExperimentRunner.Describe(exception));
            log.WriteLine($"The probe failed: {errors[^1]}");
        }

        return errors.Count == 0;
    }

    /// <summary>The longest run of whole seconds in which Windows raised no frame-arrived event at all.</summary>
    internal static int LongestSilence(IReadOnlyList<ProbeSecond> seconds)
    {
        int longest = 0;
        int current = 0;
        foreach (ProbeSecond second in seconds)
        {
            current = second.Callbacks == 0 ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private void WriteSummary(
        CaptureCounters capture,
        CapturePipeline pipeline,
        List<ProbeSecond> seconds,
        string stopped,
        long encoded,
        long encodedBytes,
        List<string> rejected,
        List<string> errors)
    {
        log.WriteLine();
        log.WriteLine("==== ExtendSpace capture probe: summary ====");
        log.WriteLine($"Stopped because: {stopped}");
        log.WriteLine("Capture API [measured]");
        log.WriteLine($"  Frame-arrived events raised by Windows:  {capture.Callbacks}");
        log.WriteLine($"    with no frame in the pool:             {capture.EmptyCallbacks}");
        log.WriteLine($"  Frames delivered:                        {capture.Delivered}");
        log.WriteLine("This program's own decisions [measured]");
        log.WriteLine($"  Skipped by the pacer (arrived early):    {capture.SkippedByPacer}");
        log.WriteLine($"  Overtaken before the encoder took them:  {capture.ReplacedBeforeTaken}");
        log.WriteLine($"  Released unencoded (--no-encode):        {pipeline.SkippedNotWanted}");
        log.WriteLine($"  No free encoder input surface:           {pipeline.DroppedNoInputSurface}");
        log.WriteLine($"  Discarded when the probe stopped:        {capture.DiscardedAtStop}");
        log.WriteLine($"  Submitted to the encoder:                {pipeline.Submitted}");
        log.WriteLine($"  Encoded (counted and discarded):         {encoded}, {encodedBytes:N0} bytes");

        if (seconds.Count > 0)
        {
            log.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Per second (n = {seconds.Count}): frames delivered min / median / max = {seconds.Min(s => s.Delivered)} / {Median(seconds.Select(s => s.Delivered))} / {seconds.Max(s => s.Delivered)}"));
            int silence = LongestSilence(seconds);
            log.WriteLine(silence == 0
                ? "Silence:         Windows raised at least one frame-arrived event in every second"
                : $"Silence:         {seconds.Count(s => s.Callbacks == 0)} second(s) with no frame-arrived event at all; the longest run was {silence} s");
        }

        if (pipeline.Capture.DeliveryIntervals is { } intervals)
        {
            log.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Interval between delivered frames: median {intervals.Median.TotalMilliseconds:0.00} ms, p95 {intervals.Percentile95.TotalMilliseconds:0.00}, max {intervals.Maximum.TotalMilliseconds:0.00} (capture API timestamps)"));
        }

        foreach (string rejection in rejected)
        {
            log.WriteLine($"Encoder not used: {rejection}");
        }

        log.WriteLine(errors.Count == 0 ? "Errors: none" : "Errors:");
        foreach (string error in errors)
        {
            log.WriteLine($"  {error}");
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        long[] sorted = [.. values.Order()];
        return sorted[(sorted.Length - 1) / 2];
    }

    private sealed record Snapshot(CaptureCounters Capture, long Submitted, long Encoded, long EncodedBytes)
    {
        public static Snapshot Take(CapturePipeline pipeline, long encoded, long encodedBytes) =>
            new(pipeline.Capture.Counters, pipeline.Submitted, encoded, encodedBytes);

        public ProbeSecond Since(Snapshot previous, double seconds, long now) => new(
            seconds,
            Capture.Callbacks - previous.Capture.Callbacks,
            Capture.EmptyCallbacks - previous.Capture.EmptyCallbacks,
            Capture.Delivered - previous.Capture.Delivered,
            Capture.SkippedByPacer - previous.Capture.SkippedByPacer,
            Capture.ReplacedBeforeTaken - previous.Capture.ReplacedBeforeTaken,
            Submitted - previous.Submitted,
            Encoded - previous.Encoded,
            EncodedBytes - previous.EncodedBytes,
            Capture.LastArrival is { } arrival ? Stopwatch.GetElapsedTime(arrival, now).TotalMilliseconds : null);
    }
}
