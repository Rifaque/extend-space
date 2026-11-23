using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Tests.ViewModels;

namespace ExtendSpace.Core.Tests.Diagnostics;

/// <summary>
/// Layer 1 of the stall design (architecture section 8.2, D67), driven by hand-made counter samples.
/// Time is given in microseconds; nothing sleeps.
/// </summary>
public sealed class StreamHealthMonitorTests
{
    private const long Second = 1_000_000;

    private readonly StreamHealthMonitor _monitor = new();
    private long _now;
    private long _captured;
    private long _submitted;
    private long _encoded;
    private long _sent;
    private long _dropped;
    private ClientCounters? _client;
    private long _clientAt;

    [Fact]
    public void The_first_sample_only_starts_the_stream()
    {
        Assert.Equal(StreamHealthState.NotStreaming, Next(0).State);
    }

    [Fact]
    public void A_stream_at_its_rate_with_every_stage_keeping_up_is_flowing()
    {
        // Client rates need two reports.
        ClientReport(0, 0);
        Next(0);
        StreamHealthReport report = Flow(Second, 60);

        Assert.Equal(StreamHealthState.Flowing, report.State);
        Assert.Equal(60, report.Rates.Captured, 3);
        Assert.Equal(60, report.Rates.Encoded, 3);
        Assert.Equal(60, report.Rates.Sent, 3);
        Assert.Equal(60, report.Rates.ClientRendered!.Value, 3);
        Assert.False(report.RequestKeyframe);
    }

    [Fact]
    public void A_still_screen_is_quiet_never_stalled_and_asks_for_nothing()
    {
        Next(0);
        Flow(Second, 60);
        for (int i = 0; i < 30; i++)
        {
            // Windows delivers nothing; nothing is submitted; the client keeps reporting nothing new.
            _now += Second;
            ClientReport(0, 0);
            StreamHealthReport report = Sample();
            Assert.Equal(StreamHealthState.Quiet, report.State);
            Assert.False(report.RequestKeyframe);
        }
    }

    [Fact]
    public void Submitted_frames_with_no_output_are_an_encoder_stall_only_from_the_threshold()
    {
        Next(0);
        Flow(Second, 60);

        _now += 400_000;
        _submitted += 24;
        _captured += 24;
        ClientReport(0, 0);
        Assert.NotEqual(StreamHealthState.EncoderStalled, Sample().State);

        _now += 100_000;
        _submitted += 6;
        _captured += 6;
        StreamHealthReport stalled = Sample();
        Assert.Equal(StreamHealthState.EncoderStalled, stalled.State);
        Assert.True(stalled.Changed);
        Assert.False(stalled.RequestKeyframe);

        // Output resumes: no longer stalled.
        _now += Second;
        _submitted += 60;
        _encoded += 90;
        _captured += 60;
        _sent += 90;
        ClientReport(90, 90);
        Assert.NotEqual(StreamHealthState.EncoderStalled, Sample().State);
    }

    [Fact]
    public void Drops_at_the_send_queue_in_three_consecutive_seconds_are_a_transport_stall_and_two_are_not()
    {
        Next(0);
        Flow(Second, 60);

        DropSecond();
        Assert.NotEqual(StreamHealthState.TransportStalled, DropSecond().State);
        Assert.Equal(StreamHealthState.TransportStalled, DropSecond().State);

        // A second without drops ends it.
        Assert.Equal(StreamHealthState.Flowing, Flow(Second, 60).State);
    }

    [Fact]
    public void A_client_that_receives_and_shows_nothing_is_stalled_after_two_seconds_and_a_keyframe_is_asked_at_most_every_two_seconds()
    {
        Next(0);
        Flow(Second, 60);
        var asks = new List<long>();
        var states = new List<StreamHealthState>();
        for (int i = 0; i < 8; i++)
        {
            _now += Second;
            Produce(60);
            ClientReport(60, 0);
            StreamHealthReport report = Sample();
            states.Add(report.State);
            if (report.RequestKeyframe)
            {
                asks.Add(_now);
            }
        }

        // Stalled from the report two seconds after the one where rendering stopped.
        Assert.Equal(StreamHealthState.Flowing, states[0]);
        Assert.Equal(StreamHealthState.ClientStalled, states[1]);
        Assert.All(states.Skip(1), state => Assert.Equal(StreamHealthState.ClientStalled, state));
        Assert.Equal(4, asks.Count);
        Assert.All(asks.Zip(asks.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 2 * Second));

        // Rendering resumes: the stall ends and nothing more is asked.
        _now += Second;
        Produce(60);
        ClientReport(60, 60);
        StreamHealthReport healthy = Sample();
        Assert.Equal(StreamHealthState.Flowing, healthy.State);
        Assert.False(healthy.RequestKeyframe);
    }

    [Fact]
    public void No_client_report_is_counted_from_the_stream_start_and_is_reported_after_two_and_a_half_seconds()
    {
        _now = 10 * Second;
        Next(0);
        _now += 2 * Second;
        Produce(120);
        Assert.Equal(StreamHealthState.Flowing, Sample().State);

        _now += Second;
        Produce(60);
        Assert.Equal(StreamHealthState.NoClientReport, Sample().State);

        _now += Second;
        Produce(60);
        ClientReport(240, 240);
        Assert.Equal(StreamHealthState.Flowing, Sample().State);
    }

    [Fact]
    public void An_encoder_stall_outranks_a_quiet_capture()
    {
        Next(0);
        Flow(Second, 60);

        // A keyframe was forced on a still screen (one repeat submitted) and nothing came out.
        _now += Second;
        _submitted += 1;
        ClientReport(0, 0);

        Assert.Equal(StreamHealthState.EncoderStalled, Sample().State);
    }

    [Fact]
    public void Client_rates_are_kept_between_reports()
    {
        // Client rates need two reports.
        ClientReport(0, 0);
        Next(0);
        Flow(Second, 60);

        _now += Second;
        Produce(60);
        StreamHealthReport noNewReport = Sample();

        Assert.Equal(60, noNewReport.Rates.ClientRendered!.Value, 3);
    }

    [Fact]
    public void A_reset_starts_a_new_stream()
    {
        Next(0);
        Flow(Second, 60);

        _monitor.Reset();

        Assert.Equal(StreamHealthState.NotStreaming, _monitor.State);
        Assert.Equal(StreamHealthState.NotStreaming, Next(Second).State);
    }

    [Fact]
    public void The_diagnostics_report_nothing_while_not_streaming_and_classify_a_stream_from_the_pipeline_counters()
    {
        var session = new SessionStateMachine();
        var pipeline = new FakePipelineCounters();
        var lines = new List<string>();
        int keyframes = 0;
        using var transport = new LoopbackTransport();
        var connection = new HostConnectionController(transport, new HostConnectionOptions { AppVersion = "1" });
        using var diagnostics = new StreamDiagnostics(new MachineSessionControl(session), connection, pipeline, () => keyframes++, lines.Add);

        Assert.Equal(StreamHealthState.NotStreaming, diagnostics.Sample(0).Health.State);

        session.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));
        session.Dispatch(new SessionEvent.TransportConnected());
        session.Dispatch(new SessionEvent.NegotiationCompleted());
        pipeline.Counters = new PipelineDiagnosticCounters(0, 0, 0, 0, 0, 0, null, 60, "1920 x 1080");

        diagnostics.Sample(Second);
        pipeline.Counters = pipeline.Counters with { SinceLastCapturedFrame = TimeSpan.FromSeconds(1) };
        StreamDiagnosticsSnapshot quiet = diagnostics.Sample(2 * Second);

        // No client report yet, and only 1 s in: quiet (a still screen), reported as such once.
        Assert.Equal(StreamHealthState.Quiet, quiet.Health.State);
        Assert.Equal(TimeSpan.FromSeconds(1), quiet.StreamingFor);
        Assert.Single(lines, line => line.StartsWith("health quiet ", StringComparison.Ordinal));
        Assert.Equal(0, keyframes);

        session.Dispatch(new SessionEvent.DisconnectRequested());
        Assert.Equal(StreamHealthState.NotStreaming, diagnostics.Sample(3 * Second).Health.State);
        Assert.Null(diagnostics.Latest!.StreamingFor);
    }

    private StreamHealthReport Next(long at)
    {
        _now = Math.Max(_now, at);
        return Sample();
    }

    private StreamHealthReport Flow(long duration, int fps)
    {
        _now += duration;
        Produce(fps);
        ClientReport(fps, fps);
        return Sample();
    }

    private StreamHealthReport DropSecond()
    {
        _now += Second;
        _captured += 60;
        _submitted += 60;
        _encoded += 60;
        _sent += 30;
        _dropped += 30;
        ClientReport(30, 30);
        return Sample();
    }

    private void Produce(int frames)
    {
        _captured += frames;
        _submitted += frames;
        _encoded += frames;
        _sent += frames;
    }

    private void ClientReport(long received, long rendered)
    {
        _client = new ClientCounters((_client?.Received ?? 0) + received, 0, 0, (_client?.Rendered ?? 0) + rendered, 0, null);
        _clientAt = _now;
    }

    private StreamHealthReport Sample() =>
        _monitor.Observe(new StreamSample(_now, 60, _captured, _submitted, _encoded, _sent, _dropped, null, _client, _clientAt));

    private sealed class FakePipelineCounters : IPipelineDiagnostics
    {
        public PipelineDiagnosticCounters? Counters { get; set; }

        public PipelineDiagnosticCounters? DiagnosticCounters => Counters;
    }
}
