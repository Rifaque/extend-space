using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Media.Encoding;
using ExtendSpace.Media.Tests.Encoding;

namespace ExtendSpace.Media.Tests;

/// <summary>
/// The pipeline's policy against a scripted backend, with the pump turned by hand and time moved by
/// hand: acquisition and release, preparation for the negotiated stream, the encoding switch, the
/// keyframe obligation (requested, forced, produced, handed to the sink), still screens, faults and
/// cancellation. Nothing here captures a display or runs an encoder.
/// </summary>
public sealed class DesktopVideoPipelineTests
{
    private static readonly EncodingRequest Preferred = StreamConfigurationPolicy.Preferred;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ScriptedBackend _backend = new();
    private readonly RecordingSink _sink = new();
    private readonly List<string> _logged = [];
    private readonly DesktopVideoPipeline _pipeline;

    public DesktopVideoPipelineTests()
    {
        _pipeline = new DesktopVideoPipeline(
            _backend,
            new DesktopVideoPipelineOptions
            {
                RaiseTimerResolution = false,
                KeyframeRetryInterval = TimeSpan.FromMilliseconds(100),
                Log = line =>
                {
                    lock (_logged)
                    {
                        _logged.Add(line);
                    }
                },
            },
            ownPumpThread: false,
            timestamp: () => _backend.Clock.Now);
    }

    private static CancellationToken None => CancellationToken.None;

    [Fact]
    public async Task Acquiring_opens_the_display_only_and_preparing_opens_an_encoder_for_exactly_the_negotiated_stream()
    {
        AttemptOutcome<VideoSourceDescription> acquired = await _pipeline.AcquireAsync(new VideoSourceRequest(null), None).Within();
        Assert.Equal(OutcomeKind.Succeeded, acquired.Kind);
        Assert.Equal(new VideoSourceDescription(2560, 1600, 165), acquired.Value);
        Assert.Equal(["open source primary"], _backend.Log);
        Assert.Empty(_backend.Source.Encoders);
        Assert.False(_backend.Source.Started);

        var negotiated = new EncodingRequest(1280, 720, 30, 6_000_000);
        AttemptOutcome<StreamSettings> prepared = await _pipeline.PrepareAsync(negotiated, None).Within();
        Assert.Equal(OutcomeKind.Succeeded, prepared.Kind);
        Assert.Equal(new StreamSettings(1280, 720, 30, 6_000_000, "Scripted Encoder", true), prepared.Value);
        Assert.Equal(negotiated, _backend.Source.Encoder.Request);
        Assert.False(_backend.Source.Started);
        Assert.Empty(_backend.Source.Encoder.Submitted);

        await _pipeline.StopAsync(None).Within();
        Assert.Equal(["open source primary", "open encoder 1280x720@30 6000000", "capture stop", "encoder dispose", "source dispose"], _backend.Log);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task A_display_that_cannot_be_acquired_is_a_terminal_capture_fault_and_leaves_nothing_behind()
    {
        _backend.AcquireFailure = new MediaSourceException(MediaFaultCodes.MonitorNotFound, "There is no monitor 3.");
        AttemptOutcome<VideoSourceDescription> outcome = await _pipeline.AcquireAsync(new VideoSourceRequest(3), None).Within();

        Assert.Equal(OutcomeKind.Failed, outcome.Kind);
        Assert.Equal(new SessionFault(FaultCategory.Capture, MediaFaultCodes.MonitorNotFound, "There is no monitor 3.", FaultTreatment.Terminal), outcome.Fault);
        Assert.True(_pipeline.IsReleased);

        // Nothing is held, so it can be acquired again at once.
        _backend.AcquireFailure = null;
        Assert.Equal(OutcomeKind.Succeeded, (await _pipeline.AcquireAsync(new VideoSourceRequest(null), None).Within()).Kind);
        await _pipeline.StopAsync(None).Within();
    }

    [Fact]
    public async Task An_unexpected_exception_while_acquiring_is_a_capture_fault_that_keeps_the_exception()
    {
        _backend.AcquireFailure = new InvalidOperationException("device lost");
        AttemptOutcome<VideoSourceDescription> outcome = await _pipeline.AcquireAsync(new VideoSourceRequest(null), None).Within();

        Assert.Equal(FaultCategory.Capture, outcome.Fault!.Category);
        Assert.Equal(MediaFaultCodes.CaptureUnavailable, outcome.Fault.Code);
        Assert.Contains("InvalidOperationException: device lost", outcome.Fault.Message, StringComparison.Ordinal);
        Assert.Equal(FaultTreatment.Terminal, outcome.Fault.Treatment);
    }

    [Fact]
    public async Task An_encoder_that_cannot_be_opened_is_a_terminal_encoder_fault_and_the_display_stays_until_stopped()
    {
        await AcquireAsync();
        _backend.Source.EncoderFailure = new MediaEncoderException(MediaFaultCodes.EncoderUnavailable, "None of the 2 H.264 encoders could be opened.");

        AttemptOutcome<StreamSettings> outcome = await _pipeline.PrepareAsync(Preferred, None).Within();
        Assert.Equal(FaultCategory.Encoder, outcome.Fault!.Category);
        Assert.Equal(MediaFaultCodes.EncoderUnavailable, outcome.Fault.Code);
        Assert.Equal(FaultTreatment.Terminal, outcome.Fault.Treatment);
        Assert.Equal(0, _backend.Source.Disposals);

        await _pipeline.StopAsync(None).Within();
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task Cancelled_before_it_begins_an_acquire_opens_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        AttemptOutcome<VideoSourceDescription> outcome = await _pipeline.AcquireAsync(new VideoSourceRequest(null), cancelled.Token).Within();
        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.Empty(_backend.Log);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task An_acquire_cancelled_while_the_display_is_being_opened_releases_it_when_it_returns()
    {
        using var gate = new ManualResetEventSlim(false);
        _backend.AcquireGate = gate;
        using var cancellation = new CancellationTokenSource();

        Task<AttemptOutcome<VideoSourceDescription>> acquiring = _pipeline.AcquireAsync(new VideoSourceRequest(null), cancellation.Token);
        await WaitUntil(() => _backend.Log.Count == 1);
        await cancellation.CancelAsync();
        gate.Set();

        Assert.Equal(OutcomeKind.Cancelled, (await acquiring.Within()).Kind);
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task A_stop_while_the_display_is_being_opened_releases_it_when_the_open_returns_and_only_then_completes()
    {
        using var gate = new ManualResetEventSlim(false);
        _backend.AcquireGate = gate;

        Task<AttemptOutcome<VideoSourceDescription>> acquiring = _pipeline.AcquireAsync(new VideoSourceRequest(null), None);
        await WaitUntil(() => _backend.Log.Count == 1);

        Task stopping = _pipeline.StopAsync(None);
        Assert.False(stopping.IsCompleted, "a stop must not report completion while the display is still being opened");
        _pipeline.RequestKeyframe();
        _pipeline.SetEncoding(true);

        gate.Set();
        Assert.Equal(OutcomeKind.Cancelled, (await acquiring.Within()).Kind);
        await stopping.Within();
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task A_stop_while_the_encoder_is_being_opened_releases_encoder_and_display_when_it_returns()
    {
        await AcquireAsync();
        using var gate = new ManualResetEventSlim(false);
        _backend.Source.EncoderGate = gate;

        Task<AttemptOutcome<StreamSettings>> preparing = _pipeline.PrepareAsync(Preferred, None);
        await WaitUntil(() => _backend.Log.Count == 2);
        Task stopping = _pipeline.StopAsync(None);
        Assert.False(stopping.IsCompleted);

        gate.Set();
        Assert.Equal(OutcomeKind.Cancelled, (await preparing.Within()).Kind);
        await stopping.Within();
        Assert.Equal(1, _backend.Source.Encoder.Disposals);
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task A_prepare_cancelled_while_the_encoder_opens_closes_that_encoder_and_keeps_the_display()
    {
        await AcquireAsync();
        using var gate = new ManualResetEventSlim(false);
        _backend.Source.EncoderGate = gate;
        using var cancellation = new CancellationTokenSource();

        Task<AttemptOutcome<StreamSettings>> preparing = _pipeline.PrepareAsync(Preferred, cancellation.Token);
        await WaitUntil(() => _backend.Log.Count == 2);
        await cancellation.CancelAsync();
        gate.Set();

        Assert.Equal(OutcomeKind.Cancelled, (await preparing.Within()).Kind);
        Assert.Equal(1, _backend.Source.Encoder.Disposals);
        Assert.Equal(0, _backend.Source.Disposals);

        await _pipeline.StopAsync(None).Within();
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.Equal(1, _backend.Source.Encoder.Disposals);
    }

    [Fact]
    public async Task Nothing_is_captured_or_encoded_before_start_and_nothing_reaches_the_sink_before_encoding_is_on()
    {
        await AcquireAndPrepareAsync();
        _pipeline.RequestKeyframe();
        Assert.False(_backend.Source.Started);

        _pipeline.Start(_sink.Sink);
        Assert.True(_backend.Source.Started);
        _backend.Source.Arrive();
        Assert.True(_pipeline.PumpOnce());

        Assert.Empty(_backend.Source.Encoder.Submitted);
        Assert.Empty(_sink.Units);
        await StopAsync();
    }

    [Fact]
    public async Task The_first_frame_once_encoding_is_on_is_forced_and_the_keyframe_reaches_the_sink()
    {
        await StartStreamingAsync();
        _backend.Source.Arrive();
        _pipeline.PumpOnce();

        Assert.Equal([(1, 0L, true)], _backend.Source.Encoder.Submitted);
        Assert.Single(_sink.Units);
        Assert.True(_sink.Units[0].Keyframe);
        VideoPipelineCounters counters = _pipeline.Counters!;
        Assert.False(counters.KeyframeOwed);
        Assert.Equal(1, counters.KeyframesDelivered);
        Assert.Equal(1, counters.KeyframesForced);
        Assert.NotNull(counters.FirstKeyframeAfterStart);

        // Later frames are ordinary ones.
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(16));
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        Assert.False(_sink.Units[1].Keyframe);
        Assert.Equal(1, _backend.Source.Encoder.Forces);
        await StopAsync();
    }

    [Fact]
    public async Task A_request_stays_owed_until_a_keyframe_has_reached_the_sink_not_merely_until_it_was_asked_of_the_encoder()
    {
        await StartStreamingAsync(holdOutput: true);
        _backend.Source.Arrive();
        _pipeline.PumpOnce();

        // Forced and submitted; the encoder has not produced it yet.
        Assert.Equal(1, _backend.Source.Encoder.Forces);
        Assert.True(_pipeline.Counters!.KeyframeOwed);
        Assert.Empty(_sink.Units);

        _pipeline.PumpOnce();
        Assert.False(_pipeline.Counters!.KeyframeOwed);
        Assert.True(_sink.Units.Single().Keyframe);
        await StopAsync();
    }

    [Fact]
    public async Task On_a_still_screen_the_last_picture_is_encoded_again_as_a_keyframe_without_waiting_for_a_change()
    {
        await StartStreamingAsync();
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        _backend.Clock.Advance(TimeSpan.FromSeconds(5));

        // The client asks; the screen does not change.
        _pipeline.RequestKeyframe();
        _pipeline.PumpOnce();

        Assert.Equal(2, _sink.Keyframes);
        Assert.Equal(1, _backend.Source.Encoder.Submitted[^1].Frame);
        Assert.True(_backend.Source.Encoder.Submitted[^1].Forced);
        Assert.Equal(1, _pipeline.Counters!.FramesRepeated);
        Assert.False(_pipeline.Counters.KeyframeOwed);

        // Once met, nothing more is repeated.
        _backend.Clock.Advance(TimeSpan.FromSeconds(1));
        _pipeline.PumpOnce();
        Assert.Equal(2, _backend.Source.Encoder.Submitted.Count);
        await StopAsync();
    }

    [Fact]
    public async Task An_encoder_that_ignores_a_force_is_forced_again_after_the_retry_interval_and_never_before()
    {
        await StartStreamingAsync();
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        _backend.Source.Encoder.IgnoreForce = true;

        _pipeline.RequestKeyframe();
        _pipeline.PumpOnce();
        Assert.Equal(2, _backend.Source.Encoder.Forces);
        Assert.True(_pipeline.Counters!.KeyframeOwed);

        _backend.Clock.Advance(TimeSpan.FromMilliseconds(99));
        _pipeline.PumpOnce();
        Assert.Equal(2, _backend.Source.Encoder.Forces);
        Assert.Equal(2, _backend.Source.Encoder.Submitted.Count);

        _backend.Source.Encoder.IgnoreForce = false;
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(1));
        _pipeline.PumpOnce();
        Assert.Equal(3, _backend.Source.Encoder.Forces);
        Assert.False(_pipeline.Counters!.KeyframeOwed);
        Assert.Equal(2, _sink.Keyframes);
        await StopAsync();
    }

    [Fact]
    public async Task The_keyframe_log_shows_when_an_obligation_opened_was_first_forced_and_was_met_and_retries_and_merged_requests_add_no_lines()
    {
        // D62. The start's own obligation: opened by StartStreamingAsync, forced and met in one turn.
        await StartStreamingAsync();
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        Assert.Equal(
            ["keyframe pipeline_owed", "keyframe pipeline_forced", "keyframe pipeline_met"],
            KeyframeLines().Select(line => line[..line.IndexOf(" t=", StringComparison.Ordinal)]));
        Assert.EndsWith(" since_owed_ms=0.0 since_first_force_ms=0.0 forced=1 requests_total=1", KeyframeLines()[2]);

        // A second obligation, which the encoder ignores once: one line when it opens, none for a
        // request merged into it or for the retry, and the met line counts both forces.
        _backend.Source.Encoder.IgnoreForce = true;
        _pipeline.RequestKeyframe();
        _pipeline.RequestKeyframe();
        _pipeline.PumpOnce();
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(99));
        _pipeline.PumpOnce();
        Assert.Equal(5, KeyframeLines().Length);
        Assert.EndsWith(" requests=2", KeyframeLines()[3]);
        Assert.EndsWith(" since_owed_ms=0.0", KeyframeLines()[4]);

        _backend.Source.Encoder.IgnoreForce = false;
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(1));
        _pipeline.PumpOnce();
        Assert.Equal(6, KeyframeLines().Length);
        Assert.StartsWith("keyframe pipeline_met ", KeyframeLines()[5]);
        Assert.EndsWith(" since_owed_ms=100.0 since_first_force_ms=100.0 forced=2 requests_total=3", KeyframeLines()[5]);
        await StopAsync();
    }

    [Fact]
    public async Task A_keyframe_handed_over_with_no_obligation_open_is_not_logged_as_meeting_one()
    {
        await StartStreamingAsync(holdOutput: true);
        _backend.Source.Arrive();
        _pipeline.PumpOnce();

        // Forced and submitted but not yet produced: owed and forced, not met.
        Assert.DoesNotContain(KeyframeLines(), line => line.StartsWith("keyframe pipeline_met", StringComparison.Ordinal));

        _pipeline.PumpOnce();
        Assert.Single(KeyframeLines(), line => line.StartsWith("keyframe pipeline_met", StringComparison.Ordinal));

        // No obligation now: a keyframe the encoder makes by itself (as its periodic ones) adds nothing.
        _backend.Source.Encoder.ForceKeyframe();
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(16));
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        _pipeline.PumpOnce();
        Assert.Equal(2, _sink.Keyframes);
        Assert.Equal(3, KeyframeLines().Length);
        await StopAsync();
    }

    [Fact]
    public async Task Many_requests_while_one_is_owed_produce_one_keyframe()
    {
        await StartStreamingAsync(holdOutput: true);
        _backend.Source.Arrive();
        for (int i = 0; i < 20; i++)
        {
            _pipeline.RequestKeyframe();
        }

        _pipeline.PumpOnce();
        _pipeline.PumpOnce();
        Assert.Equal(1, _backend.Source.Encoder.Forces);
        Assert.Equal(1, _sink.Keyframes);
        Assert.Equal(21, _pipeline.Counters!.KeyframeRequests);
        await StopAsync();
    }

    [Fact]
    public async Task With_encoding_off_frames_are_released_unencoded_and_a_request_made_meanwhile_is_met_when_encoding_resumes()
    {
        await StartStreamingAsync();
        _backend.Source.Arrive();
        _pipeline.PumpOnce();

        // The connection is lost: encoding off, frames keep coming, the client's next connection is owed a keyframe.
        _pipeline.SetEncoding(false);
        ScriptedFrame first = _backend.Source.Arrive();
        _pipeline.PumpOnce();
        ScriptedFrame second = _backend.Source.Arrive();
        _pipeline.PumpOnce();
        _pipeline.RequestKeyframe();
        _pipeline.PumpOnce();

        Assert.Single(_backend.Source.Encoder.Submitted);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
        Assert.True(_pipeline.Counters!.KeyframeOwed);

        // Back on, with the screen unchanged since: the newest picture is encoded, as a keyframe.
        _backend.Clock.Advance(TimeSpan.FromSeconds(3));
        _pipeline.SetEncoding(true);
        _pipeline.PumpOnce();
        Assert.Equal((second.Id, true), (_backend.Source.Encoder.Submitted[^1].Frame, _backend.Source.Encoder.Submitted[^1].Forced));
        Assert.True(_sink.Units[^1].Keyframe);
        Assert.False(_pipeline.Counters!.KeyframeOwed);
        await StopAsync();
    }

    [Fact]
    public async Task A_keyframe_that_comes_out_after_encoding_was_turned_off_is_discarded_and_the_obligation_stands()
    {
        await StartStreamingAsync(holdOutput: true);
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        _pipeline.SetEncoding(false);
        _pipeline.PumpOnce();

        Assert.Empty(_sink.Units);
        Assert.Equal(1, _pipeline.Counters!.AccessUnitsDiscarded);
        Assert.True(_pipeline.Counters.KeyframeOwed);
        await StopAsync();
    }

    [Fact]
    public async Task A_frame_with_no_free_encoder_surface_is_counted_and_the_next_one_is_still_encoded()
    {
        await StartStreamingAsync();
        _backend.Source.Encoder.FreeSurfaces = 0;
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        Assert.Empty(_backend.Source.Encoder.Submitted);
        Assert.Equal(1, _pipeline.Counters!.FramesNotEncoded);

        _backend.Source.Encoder.FreeSurfaces = int.MaxValue;
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(16));
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        Assert.True(_sink.Units.Single().Keyframe);
        await StopAsync();
    }

    [Fact]
    public async Task An_encoder_not_ready_for_input_holds_the_newest_frame_and_counts_the_one_it_replaced()
    {
        await StartStreamingAsync();
        _backend.Source.Encoder.CanAcceptInput = false;
        _backend.Source.Arrive();
        _pipeline.PumpOnce();
        ScriptedFrame newest = _backend.Source.Arrive();
        _pipeline.PumpOnce();

        _backend.Source.Encoder.CanAcceptInput = true;
        _pipeline.PumpOnce();
        Assert.Equal(newest.Id, _backend.Source.Encoder.Submitted.Single().Frame);
        Assert.Equal(1, _pipeline.Counters!.FramesNotEncoded);
        await StopAsync();
    }

    [Fact]
    public async Task A_capture_source_that_fails_ends_the_pump_with_a_terminal_capture_fault_and_releases_everything()
    {
        await StartStreamingAsync();
        Task<SessionFault?> watching = _pipeline.WaitForFaultAsync(None);
        _backend.Source.Failure = "The monitor's size changed from 2560 x 1600 to 1920 x 1080 during capture.";

        Assert.False(_pipeline.PumpOnce());
        SessionFault? fault = await watching.Within();
        Assert.Equal(new SessionFault(FaultCategory.Capture, MediaFaultCodes.CaptureFailed, _backend.Source.Failure, FaultTreatment.Terminal), fault);
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.Equal(1, _backend.Source.Encoder.Disposals);

        // The coordinator then stops it; that is harmless and changes nothing.
        await _pipeline.StopAsync(None).Within();
        Assert.Equal(1, _backend.Source.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_encoder_that_fails_while_running_is_a_terminal_encoder_fault(bool inSubmit)
    {
        await StartStreamingAsync();
        Task<SessionFault?> watching = _pipeline.WaitForFaultAsync(None);
        var failure = new MediaEncoderException(MediaFaultCodes.EncoderFailed, "IMFTransform::ProcessInput failed with 0x887A0005.");
        if (inSubmit)
        {
            _backend.Source.Encoder.SubmitFailure = failure;
        }
        else
        {
            _backend.Source.Encoder.EventFailure = failure;
        }

        _backend.Source.Arrive();
        Assert.False(_pipeline.PumpOnce());
        SessionFault? fault = await watching.Within();
        Assert.Equal(FaultCategory.Encoder, fault!.Category);
        Assert.Equal(MediaFaultCodes.EncoderFailed, fault.Code);
        Assert.Equal(FaultTreatment.Terminal, fault.Treatment);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task A_keyframe_that_is_not_the_configured_stream_never_reaches_the_sink_and_is_an_explicit_terminal_fault()
    {
        // D57: the encoder adapter checks every parameter set against the stream the client was told about.
        await StartStreamingAsync();
        ScriptedEncoder encoder = _backend.Source.Encoder;
        encoder.OutputCheck = new EncodedOutputCheck(Preferred.Width, Preferred.Height, H264Profile.High);
        encoder.KeyframeOutput = EncodedOutputCheckTests.Keyframe(EncodedOutputCheckTests.Nvidia720pMain);
        Task<SessionFault?> watching = _pipeline.WaitForFaultAsync(None);

        _backend.Source.Arrive();
        Assert.False(_pipeline.PumpOnce());

        SessionFault? fault = await watching.Within();
        Assert.Equal((FaultCategory.Encoder, MediaFaultCodes.EncoderOutputMismatch, FaultTreatment.Terminal), (fault!.Category, fault.Code, fault.Treatment));
        Assert.Empty(_sink.Units);
        Assert.Equal(0L, _pipeline.Counters!.KeyframesDelivered);
        Assert.Null(_pipeline.ProducedStream);
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task The_stream_read_from_the_first_checked_keyframe_is_reported()
    {
        await StartStreamingAsync();
        ScriptedEncoder encoder = _backend.Source.Encoder;
        encoder.OutputCheck = new EncodedOutputCheck(Preferred.Width, Preferred.Height, H264Profile.High);
        encoder.KeyframeOutput = EncodedOutputCheckTests.Keyframe(EncodedOutputCheckTests.Nvidia1080p);

        _backend.Source.Arrive();
        Assert.True(_pipeline.PumpOnce());

        Assert.True(Assert.Single(_sink.Units).Keyframe);
        Assert.Equal("1920 x 1080, High profile, level 4.2, 60 fps signalled", _pipeline.ProducedStream);
    }

    [Fact]
    public async Task Stopping_releases_frames_encoder_display_once_each_in_reverse_order_and_nothing_reaches_the_sink_after()
    {
        await StartStreamingAsync(holdOutput: true);
        ScriptedFrame frame = _backend.Source.Arrive();
        _pipeline.PumpOnce();
        Task<SessionFault?> watching = _pipeline.WaitForFaultAsync(None);

        await _pipeline.StopAsync(None).Within();
        await _pipeline.StopAsync(None).Within();
        Assert.Null(await watching.Within());
        Assert.False(_pipeline.PumpOnce());

        Assert.Empty(_sink.Units);
        Assert.Equal(1, frame.Disposals);
        Assert.Equal(1, _backend.Source.Encoder.Disposals);
        Assert.Equal(1, _backend.Source.Disposals);
        Assert.Equal(["capture stop", "encoder dispose", "source dispose"], _backend.Log.TakeLast(3));

        // Requests and switches after the stop are ignored.
        _pipeline.RequestKeyframe();
        _pipeline.SetEncoding(true);
        Assert.Equal(1, _pipeline.Counters!.KeyframeRequests);
    }

    [Fact]
    public async Task Requests_before_acquisition_are_ignored_and_the_pipeline_can_be_acquired_again_after_a_stop()
    {
        _pipeline.RequestKeyframe();
        _pipeline.SetEncoding(true);
        Assert.Null(await _pipeline.WaitForFaultAsync(None).Within());

        for (int session = 0; session < 3; session++)
        {
            await StartStreamingAsync();
            _backend.Source.Arrive();
            _pipeline.PumpOnce();
            Assert.True(_sink.Units[^1].Keyframe, $"session {session}");
            await StopAsync();
        }

        Assert.Equal(3, _backend.Sources.Count);
        Assert.All(_backend.Sources, source => Assert.Equal(1, source.Disposals));
        Assert.All(_backend.Sources, source => Assert.Equal(1, source.Encoder.Disposals));
    }

    [Fact]
    public async Task Capture_times_handed_to_the_sink_are_the_frames_hand_over_on_the_monotonic_clock()
    {
        await StartStreamingAsync();
        ScriptedFrame frame = _backend.Source.Arrive();
        _pipeline.PumpOnce();

        long expected = (long)((Int128)frame.Arrival * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal(expected, _sink.Units.Single().CaptureMicros);
        await StopAsync();
    }

    [Fact]
    public async Task Calls_out_of_order_are_refused_rather_than_guessed_at()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.PrepareAsync(Preferred, None));
        Assert.Throws<InvalidOperationException>(() => _pipeline.Start(_sink.Sink));
        await AcquireAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.AcquireAsync(new VideoSourceRequest(null), None));
        Assert.Throws<InvalidOperationException>(() => _pipeline.Start(_sink.Sink));
        await StopAsync();
    }

    [Fact]
    public async Task With_its_own_pump_thread_it_streams_and_a_stop_ends_the_thread_and_releases_everything_repeatedly()
    {
        var backend = new ScriptedBackend();
        var pipeline = new DesktopVideoPipeline(backend, new DesktopVideoPipelineOptions { RaiseTimerResolution = false, PollInterval = TimeSpan.FromMilliseconds(5) }, ownPumpThread: true, timestamp: null);
        for (int round = 0; round < 20; round++)
        {
            var sink = new RecordingSink();
            Assert.Equal(OutcomeKind.Succeeded, (await pipeline.AcquireAsync(new VideoSourceRequest(null), None).Within()).Kind);
            Assert.Equal(OutcomeKind.Succeeded, (await pipeline.PrepareAsync(Preferred, None).Within()).Kind);
            pipeline.Start(sink.Sink);
            pipeline.SetEncoding(true);
            pipeline.RequestKeyframe();
            backend.Source.Arrive();
            await WaitUntil(() => sink.Keyframes == 1);

            // Frames arriving from another thread while the pump runs and the stop lands.
            using var feeding = new CancellationTokenSource();
            ScriptedSource source = backend.Source;
            Task feeder = Task.Run(async () =>
            {
                while (!feeding.IsCancellationRequested)
                {
                    source.Arrive();
                    await Task.Yield();
                }
            }, TestContext.Current.CancellationToken);

            await pipeline.StopAsync(None).Within();
            int delivered = sink.Units.Count;
            await feeding.CancelAsync();
            await feeder.Within();

            Assert.True(pipeline.IsReleased, $"round {round}");
            Assert.Equal(1, source.Disposals);
            Assert.Equal(1, source.Encoder.Disposals);
            Assert.Equal(delivered, sink.Units.Count);
        }
    }

    private async Task AcquireAsync() =>
        Assert.Equal(OutcomeKind.Succeeded, (await _pipeline.AcquireAsync(new VideoSourceRequest(null), None).Within()).Kind);

    private async Task AcquireAndPrepareAsync()
    {
        await AcquireAsync();
        Assert.Equal(OutcomeKind.Succeeded, (await _pipeline.PrepareAsync(Preferred, None).Within()).Kind);
    }

    /// <summary>As the coordinator enters Streaming: start, encoding on, a keyframe asked for.</summary>
    private async Task StartStreamingAsync(bool holdOutput = false)
    {
        await AcquireAndPrepareAsync();
        _backend.Source.Encoder.HoldOutput = holdOutput;
        _pipeline.Start(_sink.Sink);
        _pipeline.SetEncoding(true);
        _pipeline.RequestKeyframe();
    }

    private string[] KeyframeLines()
    {
        lock (_logged)
        {
            return [.. _logged.Where(line => line.StartsWith("keyframe pipeline_", StringComparison.Ordinal))];
        }
    }

    private async Task StopAsync()
    {
        await _pipeline.StopAsync(None).Within();
        Assert.True(_pipeline.IsReleased);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "the condition was not reached in time");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }
}
