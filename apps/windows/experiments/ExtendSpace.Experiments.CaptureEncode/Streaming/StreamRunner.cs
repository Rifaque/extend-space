using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ExtendSpace.Experiments.CaptureEncode.Capture;
using ExtendSpace.Experiments.CaptureEncode.Measurement;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>
/// Runs the capture pipeline into a live session with the Android app. Like the file runner it
/// always returns a report; failures are recorded in it with whatever was measured before them.
/// </summary>
internal sealed class StreamRunner(CaptureOptions options, StreamOptions stream, TextWriter log)
{
    private const int SendQueueCapacity = 4;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinalStatsWait = TimeSpan.FromSeconds(3);

    private readonly Lock _logGate = new();
    private readonly long _runStart = Stopwatch.GetTimestamp();

    public StreamReport Run(CancellationToken cancellation)
    {
        var report = new StreamReport(options, stream);
        try
        {
            RunStream(report, cancellation);
        }
        catch (Exception exception)
        {
            report.Errors.Add(ExperimentRunner.Describe(exception));
        }

        return report;
    }

    private void RunStream(StreamReport report, CancellationToken cancellation)
    {
        using var wake = new WakeSignal();
        var hostDelay = new DurationSeries();
        HostSession? session = null;
        CapturePipeline? opened = null;

        using CapturePipeline pipeline = CapturePipeline.Open(
            options,
            wake,
            (data, sampleTime) =>
            {
                // A frame's timestamp on the wire is the moment Windows handed it to this process,
                // read on the performance counter. Windows' own frame timestamp is on the same
                // clock but was measured to run a few milliseconds ahead of the hand-over, so it
                // is not a moment that latency can be counted from.
                long captureMicros = opened!.TakeArrival(sampleTime) is { } arrival
                    ? HostClock.ToMicros(arrival)
                    : HostClock.ToMicros(opened.FirstFrameTime!.Value + TimeSpan.FromTicks(sampleTime));
                hostDelay.Add(TimeSpan.FromMicroseconds(HostClock.NowMicros - captureMicros));
                session!.OfferAccessUnit(data, captureMicros);
            },
            report.Pipeline.RejectedEncoders);
        pipeline.KeepArrivalTimes = true;
        opened = pipeline;
        ExperimentRunner.DescribePipeline(report.Pipeline, pipeline);

        var config = new StreamConfig(
            "h264",
            options.Profile.ToString().ToLowerInvariant(),
            pipeline.Geometry.OutputWidth,
            pipeline.Geometry.OutputHeight,
            options.FrameRate,
            options.BitrateBitsPerSecond,
            Protocol.MaxVideoPayload,
            pipeline.Encoder.Evidence.Name,
            pipeline.Acceleration.Status.ToString().ToLowerInvariant());
        report.Config = config;

        using AdbForward? forward = stream.ManageAdbForward ? AdbForward.Create(stream.AdbPath, stream.DeviceSerial, stream.Port) : null;
        report.Transport = forward is null
            ? $"127.0.0.1:{stream.Port}, forwarded by the user (--no-adb-forward)"
            : $"127.0.0.1:{stream.Port}, set up with '{forward.Description}' and removed at the end";
        Log(report, $"transport: {report.Transport}");

        using TimerResolution timer = options.RaiseTimerResolution ? TimerResolution.Request() : TimerResolution.LeaveDefault();
        report.Pipeline.TimerResolution = timer.Description;

        string version = typeof(StreamRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        session = new HostSession(
            new HostSessionOptions(stream.Port, stream.ReconnectBudget, SendQueueCapacity, Environment.MachineName, version),
            () => config,
            message => Log(report, message),
            wake.Set);

        using Process process = Process.GetCurrentProcess();
        TimeSpan processorTimeAtStart = process.TotalProcessorTime;

        try
        {
            session.Start();
            pipeline.Start();
            Log(report, $"capturing {pipeline.Monitor.DeviceName}, encoding {config.Width} x {config.Height} at {config.FrameRate} fps with '{config.Encoder}'; waiting for the client");
            StreamLoop(report, pipeline, session, wake, cancellation);
        }
        catch (Exception exception)
        {
            report.StopReason ??= "an error occurred during capture, encoding or sending";
            report.Errors.Add(ExperimentRunner.Describe(exception));
        }

        pipeline.StopCapture();
        report.StreamWindowEnd = Stopwatch.GetTimestamp();
        bool encoderFailed = report.Errors.Any(error => error.StartsWith(nameof(Encoding.EncoderException), StringComparison.Ordinal));
        if (!encoderFailed)
        {
            pipeline.Drain(wake, report.Errors);
        }

        WaitForFinalClientStats(session);
        CollectWindow(report, pipeline, session, atStart: false);
        session.Stop(report.StopReason ?? "the host stopped the stream");

        process.Refresh();
        report.ProcessorTime = process.TotalProcessorTime - processorTimeAtStart;
        report.ProcessorTimeWindow = Stopwatch.GetElapsedTime(_runStart);
        report.PeakWorkingSetBytes = process.PeakWorkingSet64;

        report.FinalPhase = session.Phase;
        report.Client = session.ClientHello;
        report.Capabilities = session.ClientCapabilities;
        report.Ready = session.ClientReady;
        report.Host = session.Counters;
        report.ClientTotals = session.ClientTotals;
        report.Clock = session.Clock;
        report.Latency = session.Latency;
        report.HostDelay = hostDelay.Summarize();
        report.DeliveryDelay = pipeline.Capture.DeliveryDelays;
        report.EncoderTurnaround = pipeline.Encoder.Turnaround.Summarize();
        report.KeyframeRequestsAccepted = pipeline.KeyframeRequestsAccepted;
        report.KeyframeRequestsRefused = pipeline.KeyframeRequestsRefused;

        CaptureCounters capture = pipeline.Capture.Counters;
        report.Frames = new FrameCounters
        {
            Delivered = capture.Delivered,
            SkippedByPacer = capture.SkippedByPacer,
            DroppedEncoderBusy = capture.ReplacedBeforeTaken,
            DroppedNoInputSurface = pipeline.DroppedNoInputSurface,
            DiscardedAtStop = capture.DiscardedAtStop,
            Submitted = pipeline.Submitted,
            Encoded = report.Host.Encoded,
        };
        report.SkippedNoClient = pipeline.SkippedNotWanted;

        if (session.Phase == SessionPhase.Failed && session.EndReason is { } failure)
        {
            report.Errors.Add($"The session failed: {failure}");
        }
    }

    private void StreamLoop(StreamReport report, CapturePipeline pipeline, HostSession session, WakeSignal wake, CancellationToken cancellation)
    {
        long? streamStart = null;
        long lastTick = Stopwatch.GetTimestamp();
        long nextSample = lastTick + Stopwatch.Frequency;
        var previous = new Snapshot(pipeline, session);

        while (true)
        {
            bool streaming = session.IsStreaming;
            pipeline.Pump(encode: streaming);
            if (streaming && session.TakeKeyframeRequest())
            {
                pipeline.RequestKeyframe();
            }

            long now = Stopwatch.GetTimestamp();
            if (streaming)
            {
                if (streamStart is null)
                {
                    streamStart = now;
                    report.WaitedForClient = Stopwatch.GetElapsedTime(_runStart, now);
                    report.StreamWindowStart = now;
                    CollectWindow(report, pipeline, session, atStart: true);
                    previous = new Snapshot(pipeline, session);
                    nextSample = now + Stopwatch.Frequency;
                }

                report.TimeStreaming += Stopwatch.GetElapsedTime(lastTick, now);
            }

            lastTick = now;

            if (streamStart is not null && now >= nextSample)
            {
                var current = new Snapshot(pipeline, session);
                SecondSample sample = current.Since(previous, Stopwatch.GetElapsedTime(streamStart.Value, now), session);
                report.Seconds.Add(sample);
                WriteLine(sample.Describe());
                previous = current;
                nextSample += (long)(SampleInterval.TotalSeconds * Stopwatch.Frequency);
            }

            if (pipeline.Capture.Failure is { } captureFailure)
            {
                report.StopReason = "the capture source failed";
                report.Errors.Add(captureFailure);
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                report.StopReason = "cancelled by the user (Ctrl+C or Ctrl+Break)";
                return;
            }

            if (session.HasEnded)
            {
                report.StopReason = session.EndReason ?? "the session ended";
                return;
            }

            if (streamStart is { } started)
            {
                TimeSpan remaining = options.Duration - Stopwatch.GetElapsedTime(started, now);
                if (remaining <= TimeSpan.Zero)
                {
                    report.StopReason = "the requested duration elapsed";
                    return;
                }
            }

            wake.Wait(CapturePipeline.PollInterval);
        }
    }

    /// <summary>
    /// Gives the client's once-a-second statistics time to catch up with what was sent, so that the
    /// totals in the report are from after the last frame rather than up to a second before it.
    /// </summary>
    private static void WaitForFinalClientStats(HostSession session)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(FinalStatsWait.TotalSeconds * Stopwatch.Frequency);
        bool Caught() => session.QueueDepth == 0
            && (session.LatestClientSample?.Stats.FramesReceived ?? 0) >= session.TransmittedThisConnection;

        while (session.IsStreaming && !Caught() && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(20);
        }

        // One more report, so that decoding and rendering of those last frames are counted too.
        long? seen = session.LatestClientSample?.ArrivedMicros;
        while (session.IsStreaming && session.LatestClientSample?.ArrivedMicros == seen && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(20);
        }
    }

    private static void CollectWindow(StreamReport report, CapturePipeline pipeline, HostSession session, bool atStart)
    {
        var counts = new WindowCounts(
            pipeline.Capture.Counters.Delivered,
            pipeline.Submitted,
            session.Counters.Encoded,
            session.Counters.Transmitted,
            session.Counters.TransmittedBytes);
        if (atStart)
        {
            report.WindowAtStart = counts;
        }
        else
        {
            report.WindowAtEnd = counts;
        }
    }

    private void Log(StreamReport report, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"[{Stopwatch.GetElapsedTime(_runStart).TotalSeconds,7:0.000} s] {message}");
        lock (_logGate)
        {
            report.Events.Add(line);
        }

        WriteLine(line);
    }

    private void WriteLine(string line)
    {
        lock (_logGate)
        {
            log.WriteLine(line);
        }
    }

    /// <summary>The cumulative counters at one instant, for turning into per-second rates.</summary>
    private sealed class Snapshot(CapturePipeline pipeline, HostSession session)
    {
        private readonly long _timestamp = Stopwatch.GetTimestamp();
        private readonly long _delivered = pipeline.Capture.Counters.Delivered;
        private readonly HostStreamCounters _host = session.Counters;

        public SecondSample Since(Snapshot previous, TimeSpan sinceStreamStart, HostSession session)
        {
            double seconds = Stopwatch.GetElapsedTime(previous._timestamp, _timestamp).TotalSeconds;
            ClientStatsSample? latest = session.LatestClientSample;
            ClientStatsSample? before = session.PreviousClientSample;

            double? ClientRate(Func<ClientStats, long> counter) =>
                latest is not null && before is not null && latest.ArrivedMicros > before.ArrivedMicros
                    ? (counter(latest.Stats) - counter(before.Stats)) * 1_000_000.0 / (latest.ArrivedMicros - before.ArrivedMicros)
                    : null;

            return new SecondSample
            {
                StreamSeconds = sinceStreamStart.TotalSeconds,
                Phase = session.Phase,
                CaptureFps = (_delivered - previous._delivered) / seconds,
                EncodedFps = (_host.Encoded - previous._host.Encoded) / seconds,
                TransmittedFps = (_host.Transmitted - previous._host.Transmitted) / seconds,
                BitsPerSecond = (_host.TransmittedBytes - previous._host.TransmittedBytes) * 8 / seconds,
                HostDropped = (_host.DroppedQueueFull + _host.DroppedAwaitingKeyframe + _host.DiscardedAtDisconnect + _host.DroppedTooLarge)
                    - (previous._host.DroppedQueueFull + previous._host.DroppedAwaitingKeyframe + previous._host.DiscardedAtDisconnect + previous._host.DroppedTooLarge),
                QueueDepth = session.QueueDepth,
                ReceivedFps = ClientRate(stats => stats.FramesReceived),
                DecodedFps = ClientRate(stats => stats.FramesDecoded),
                RenderedFps = ClientRate(stats => stats.FramesRendered),
                ClientMissing = latest?.Stats.FramesMissing,
                ClientDiscarded = latest?.Stats.FramesDiscarded,
                RenderLatency = latest?.Stats.RenderLatency,
                RoundTripMicros = session.Clock?.RoundTripMicros,
            };
        }
    }
}
