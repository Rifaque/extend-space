using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Media.Tests.Device;

/// <summary>
/// Stage 4 acceptance on hardware: the production session end to end. The production coordinator,
/// <see cref="HostConnectionController"/>, <see cref="UsbTransport"/>, the real adb and the real
/// <see cref="DesktopVideoPipeline"/> (capture and Media Foundation encoding on this PC), composed as
/// <see cref="HostComposition"/> composes them, against the tablet running the **production** client
/// (the debug build's session-test screen, which drives the app's own controller, USB link and
/// MediaCodec receiver; not the experiment's live screen).
///
/// What the client did is read from its own <c>stats</c> messages, which the connection keeps
/// (<see cref="HostConnectionController.LatestClientReport"/>): frames received, decoded and released
/// to its surface. Whether the picture is visible is checked separately, by a screenshot of the tablet.
/// The lifecycle tests (stage 4.1) act on the tablet through adb themselves (key events, the rotation
/// setting, freezing the app's own process) and restore what they change.
///
/// Skipped unless <c>EXTENDSPACE_DEVICE_TESTS=1</c>. Each test is run alone, writing a log to
/// <c>artifacts/device-acceptance/</c>. The device is <c>ANDROID_SERIAL</c> or the only one attached.
/// </summary>
[Trait("Category", "Device")]
public sealed class ProductionVideoAcceptanceTests : IDisposable
{
    private readonly Evidence _evidence;

    public ProductionVideoAcceptanceTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_DEVICE_TESTS") == "1", "device acceptance: set EXTENDSPACE_DEVICE_TESTS=1 with the tablet attached and its session-test screen open");
        _evidence = new Evidence(TestContext.Current.Test?.TestDisplayName ?? "device-test");
    }

    /// <summary>How long the streaming tests stream; <c>EXTENDSPACE_STREAM_SECONDS</c>, 30 by default.</summary>
    private static int StreamSeconds => Seconds("EXTENDSPACE_STREAM_SECONDS", 30);

    /// <summary>How long a test waits for an operator (a cable, an unlock); generous, since the operator reads the prompt first.</summary>
    private static readonly TimeSpan OperatorWait = TimeSpan.FromSeconds(180);

    public void Dispose() => _evidence?.Dispose();

    /// <summary>V19a. First frame, then a sustained stream with per-second figures, then stop. With a long <c>EXTENDSPACE_STREAM_SECONDS</c> it is the soak (V10).</summary>
    [Fact]
    public async Task V19a_the_production_path_delivers_a_keyframe_the_client_decodes_then_streams_and_stops_cleanly()
    {
        using var rig = new Rig(_evidence);
        await StreamThenStopAsync(rig, StreamConfigurationPolicy.Preferred);
    }

    /// <summary>
    /// V19g (V2). The fallback configuration end to end: 1280 x 720 at 30 fps and 6 Mbit/s. The tablet
    /// decodes 1080p60, so the policy would not fall back on its own; the session here chooses the
    /// fallback, still only if the client says it can decode it. The encoder is opened for it, its own
    /// parameter set must say so (D57), and the client decodes and draws it.
    /// </summary>
    [Fact]
    public async Task V19g_the_fallback_configuration_is_negotiated_encoded_decoded_and_stopped()
    {
        EncodingRequest fallback = StreamConfigurationPolicy.Fallback;
        using var rig = new Rig(_evidence, new SessionCoordinatorOptions
        {
            ChooseEncoding = (_, client) => client.CanDecode(fallback.Width, fallback.Height) ? fallback : null,
        });
        await StreamThenStopAsync(rig, fallback);
    }

    /// <summary>V19b. Ten sessions in a row, each with a first keyframe the client decodes; nothing left behind.</summary>
    [Fact]
    public async Task V19b_ten_sessions_each_decode_a_first_keyframe_and_release_everything()
    {
        using var rig = new Rig(_evidence);
        var ports = new List<int>();
        for (int round = 0; round < 10; round++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Assert.Equal(SessionStartResult.Started, rig.Start());
            await rig.WaitForAsync(s => s.Phase == SessionPhase.Streaming, TimeSpan.FromSeconds(30));
            await rig.UntilAsync(() => rig.Connection.LatestClientReport?.FramesRendered > 0, TimeSpan.FromSeconds(10), $"round {round}: nothing rendered");
            long rendered = watch.ElapsedMilliseconds;
            ports.Add(rig.ForwardedPort!.Value);
            long stopAt = Stopwatch.GetTimestamp();
            Assert.True(rig.Coordinator.Stop());
            await rig.WaitForAsync(s => s.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15));
            _evidence.Line($"round {round}: start to first client report of a rendered frame {rendered} ms; stop to Disconnected {Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds:0} ms; repeated frames {rig.Pipeline.Counters!.FramesRepeated}; encoder output {rig.Pipeline.ProducedStream}");
            Assert.True(rig.Pipeline.IsReleased, $"round {round}");
            Assert.True(rig.Coordinator.IsReleased, $"round {round}");
            Assert.Empty(rig.Coordinator.LastTeardownFaults);
        }

        string[] forwards = await rig.ForwardListAsync();
        _evidence.Line($"ports: {string.Join(", ", ports)}");
        Assert.DoesNotContain(forwards, line => ports.Any(port => line.Contains($"tcp:{port} ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V19c. Needs an operator: pulls the USB cable once the session is streaming and plugs it back
    /// within the 30 s budget. The session must come back and the client decode a fresh keyframe.
    /// With moving content it is F1; on a still screen it is F2 (V3).
    /// </summary>
    [Fact]
    public async Task V19c_after_a_cable_pull_the_session_recovers_and_the_client_decodes_a_fresh_keyframe()
    {
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        await using var watch = new DeviceWatch(_evidence, rig.RequireSerial());
        long keyframesBefore = rig.Pipeline.Counters!.KeyframesDelivered;
        _evidence.Line("pull the cable now, and plug it back in within 30 s");
        Console.WriteLine("V19c: pull the USB cable now, then plug it back in within 30 s.");

        SessionState lost = await rig.WaitForAsync(s => s.Phase == SessionPhase.Reconnecting, OperatorWait);
        long lostAt = Stopwatch.GetTimestamp();
        _evidence.Line($"loss seen: {lost.LastAttemptFault?.Code}; the device watch saw the tablet go {Since(watch.GoneAt, lostAt)} before");
        SessionState back = await rig.WaitForAsync(s => s.Phase is SessionPhase.Streaming or SessionPhase.Failed, TimeSpan.FromSeconds(60));
        long backAt = Stopwatch.GetTimestamp();
        _evidence.Line($"{back.Phase} {Stopwatch.GetElapsedTime(lostAt, backAt).TotalMilliseconds:0} ms after the loss; {(watch.BackAt is { } replugged ? $"{Stopwatch.GetElapsedTime(replugged, backAt).TotalMilliseconds:0} ms after adb listed the tablet again" : "the device watch did not see the tablet return")}; error {back.Error?.Cause?.Code ?? "none"}");
        Assert.Equal(SessionPhase.Streaming, back.Phase);

        long renderedBefore = rig.Connection.LatestClientReport?.FramesRendered ?? 0;
        await rig.UntilAsync(() => rig.Connection.LatestClientReport?.FramesRendered > 0 && rig.Connection.LatestClientReport.ReceivedMicros > MonotonicClockMicros(backAt), TimeSpan.FromSeconds(10), "nothing rendered after recovery");
        VideoPipelineCounters after = rig.Pipeline.Counters!;
        _evidence.Line($"after recovery: keyframes delivered {keyframesBefore} -> {after.KeyframesDelivered}, repeated {after.FramesRepeated}; client {rig.Connection.LatestClientReport} (before the loss it had rendered {renderedBefore})");
        Assert.True(after.KeyframesDelivered > keyframesBefore);
        await rig.SampleAsync(5);
        await rig.StopAndCheckCleanAsync();
    }

    /// <summary>
    /// V19h (F3, part of V11). Needs an operator: pulls the cable and keeps it out for longer than the
    /// 30 s budget. The session must fail with the budget's fault, release everything it owns, and
    /// then try nothing more: no state change and no adb command for the next 10 s.
    /// </summary>
    [Fact]
    public async Task V19h_a_cable_out_for_longer_than_the_budget_fails_the_session_releases_everything_and_stops_retrying()
    {
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        await using var watch = new DeviceWatch(_evidence, rig.RequireSerial());
        _evidence.Line("pull the cable now, and keep it out until told (about 45 s)");
        Console.WriteLine("V19h: pull the USB cable now and keep it out until the log says to plug it back in.");

        await rig.WaitForAsync(s => s.Phase == SessionPhase.Reconnecting, OperatorWait);
        long lostAt = Stopwatch.GetTimestamp();
        SessionState end = await rig.WaitForAsync(s => s.Phase is SessionPhase.Failed or SessionPhase.Streaming, TimeSpan.FromSeconds(60));
        _evidence.Line($"{end.Phase} {Stopwatch.GetElapsedTime(lostAt).TotalMilliseconds:0} ms after the loss: {end.Error?.Code}/{end.Error?.Cause?.Code}: {end.Error?.Cause?.Message} ({end.Error?.Detail})");
        Assert.True(end.Phase == SessionPhase.Failed, $"the cable came back within the budget: the session reached {end.Phase}");

        await rig.UntilAsync(() => rig.Pipeline.IsReleased && rig.Coordinator.IsReleased, TimeSpan.FromSeconds(15), "not released after the failure");
        _evidence.Line($"released; teardown faults: {string.Join("; ", rig.Coordinator.LastTeardownFaults.Select(f => $"{f.Code}: {f.Message}"))}");

        // Nothing goes on trying: no state change and no adb command for the next 10 s.
        int phases = rig.PhaseChanges;
        int commands = rig.AdbCommands;
        var quiet = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromSeconds(10))
        {
            Assert.Equal(phases, rig.PhaseChanges);
            Assert.Equal(commands, rig.AdbCommands);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        _evidence.Line("no state change and no adb command in the 10 s after the failure; plug the cable back in now");
        Console.WriteLine("V19h: plug the USB cable back in now.");
        await rig.UntilAsync(() => watch.BackAt is not null, OperatorWait, "the tablet did not come back");
        Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{rig.ForwardedPort} ", StringComparison.Ordinal));
        Assert.Equal(phases, rig.PhaseChanges);
    }

    /// <summary>
    /// V19d (C2; V4 when the PC's screen is still). Home, then the session screen again, while
    /// streaming: the session stays Streaming, the client draws nothing while it has no surface, asks
    /// for a keyframe when the surface is lent again, and draws from it on a new decoder.
    /// </summary>
    [Fact]
    public async Task V19d_home_and_return_withdraws_and_lends_the_surface_and_the_picture_resumes_from_a_fresh_keyframe()
    {
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        await rig.SampleAsync(3);
        int phases = rig.PhaseChanges;

        await rig.SurfaceCycleAsync("Home", ["input", "keyevent", "KEYCODE_HOME"], ["am", "start", "-n", Rig.SessionScreen], awayFor: TimeSpan.FromSeconds(3));
        await rig.SampleAsync(3);
        await rig.SurfaceCycleAsync("Home again", ["input", "keyevent", "KEYCODE_HOME"], ["am", "start", "-n", Rig.SessionScreen], awayFor: TimeSpan.FromSeconds(10));
        await rig.SampleAsync(3);

        Assert.Equal(phases, rig.PhaseChanges);
        await rig.StopAndCheckCleanAsync();
    }

    /// <summary>
    /// V19e (C3, V5). Rotation while streaming: the screen is recreated, its old surface withdrawn and
    /// the new one lent; the session stays Streaming and the picture resumes on the new surface. The
    /// tablet's rotation settings are read first and restored afterwards; with auto-rotate on, the
    /// test cannot rotate by setting and reports itself blocked.
    /// </summary>
    [Fact]
    public async Task V19e_rotation_recreates_the_screen_and_the_picture_resumes_on_the_new_surface()
    {
        using var rig = new Rig(_evidence);
        string autoRotate = (await rig.ShellAsync("settings", "get", "system", "accelerometer_rotation")).Trim();
        string rotation = (await rig.ShellAsync("settings", "get", "system", "user_rotation")).Trim();
        _evidence.Line($"tablet rotation settings: accelerometer_rotation {autoRotate}, user_rotation {rotation}");
        Assert.SkipUnless(autoRotate == "0" && rotation is "0" or "1" or "2" or "3", "BLOCKED: rotation can be set by adb only with auto-rotate off");
        string other = rotation is "1" or "3" ? "0" : "1";
        try
        {
            await rig.StartStreamingAndRenderingAsync();
            await rig.SampleAsync(3);
            int phases = rig.PhaseChanges;

            await rig.SurfaceCycleAsync($"rotation {rotation} -> {other}", ["settings", "put", "system", "user_rotation", other], back: null, awayFor: TimeSpan.Zero);
            await rig.SampleAsync(3);
            await rig.SurfaceCycleAsync($"rotation {other} -> {rotation}", ["settings", "put", "system", "user_rotation", rotation], back: null, awayFor: TimeSpan.Zero);
            await rig.SampleAsync(3);

            Assert.Equal(phases, rig.PhaseChanges);
            await rig.StopAndCheckCleanAsync();
        }
        finally
        {
            await rig.ShellAsync("settings", "put", "system", "user_rotation", rotation);
            _evidence.Line($"user_rotation restored to {(await rig.ShellAsync("settings", "get", "system", "user_rotation")).Trim()}");
        }
    }

    /// <summary>
    /// V19f (C4, V6). Screen off for <c>EXTENDSPACE_SCREEN_OFF_SECONDS</c> (10 by default), then woken;
    /// an operator unlocks the tablet. The architecture commits to this only as "absence of the UI":
    /// the output is withdrawn, and the client ends the session with a goodbye if it stays without one
    /// for its 30 s grace period. So either outcome can be right; the test records which happened and
    /// checks that it was one of those two, cleanly: the picture back from a fresh keyframe, or an
    /// orderly end with nothing left behind. Never a failure, never a decoder error.
    /// </summary>
    [Fact]
    public async Task V19f_screen_off_and_back_either_resumes_from_a_fresh_keyframe_or_ends_in_an_orderly_goodbye()
    {
        int offSeconds = Seconds("EXTENDSPACE_SCREEN_OFF_SECONDS", 10);
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        await rig.SampleAsync(2);
        ClientReport before = rig.Connection.LatestClientReport!;
        long wanted = rig.Connection.Counters.KeyframesWanted;

        _evidence.Line($"screen off for {offSeconds} s");
        long offAt = Stopwatch.GetTimestamp();
        await rig.ShellAsync("input", "keyevent", "KEYCODE_SLEEP");
        for (int second = 0; second < offSeconds && rig.Coordinator.State.Phase == SessionPhase.Streaming; second++)
        {
            // What the tablet itself says each second: whether it is asleep, and whether the lock screen shows.
            string power = await rig.ShellAsync("dumpsys", "power");
            string window = await rig.ShellAsync("dumpsys", "window");
            string Field(string text, string name) => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal)) ?? $"{name}?";
            _evidence.Line($"tablet: {Field(power, "mWakefulness=")}, {Field(window, "isKeyguardShowing=")}, {Field(window, "mCurrentFocus=")}");
            await rig.SampleAsync(1, stopWhenNotStreaming: true);
        }

        await rig.ShellAsync("input", "keyevent", "KEYCODE_WAKEUP");
        _evidence.Line("screen woken; unlock the tablet now");
        Console.WriteLine("V19f: unlock the tablet now.");

        Rig.Resumption? resumed = null;
        if (rig.Coordinator.State.Phase == SessionPhase.Streaming)
        {
            resumed = await rig.WaitForResumeAsync("screen on", wanted, OperatorWait, giveUpWhen: () => rig.Coordinator.State.Phase != SessionPhase.Streaming);
        }

        SessionState state = rig.Coordinator.State;
        ClientReport? after = rig.Connection.LatestClientReport;
        _evidence.Line($"outcome {Stopwatch.GetElapsedTime(offAt).TotalSeconds:0.0} s after the screen went off: {(resumed is not null ? "the picture resumed" : $"the session is {state.Phase}")}; error {state.Error?.Code.ToString() ?? "none"}; client decoder errors {after?.DecoderErrors}");
        Assert.NotEqual(SessionPhase.Failed, state.Phase);
        Assert.Equal(before.DecoderErrors, after?.DecoderErrors ?? before.DecoderErrors);
        if (resumed is not null)
        {
            await rig.SampleAsync(3);
            await rig.StopAndCheckCleanAsync();
        }
        else
        {
            SessionState ended = await rig.WaitForAsync(s => s.Phase is SessionPhase.Disconnected or SessionPhase.Failed, TimeSpan.FromSeconds(15));
            Assert.Equal(SessionPhase.Disconnected, ended.Phase);
            Assert.Null(ended.Error);
            Assert.True(rig.Pipeline.IsReleased);
            Assert.True(rig.Coordinator.IsReleased);
            Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{rig.ForwardedPort} ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// V19i (F4, the frozen app of V11). The app's own process is stopped (SIGSTOP through
    /// <c>run-as</c>, debug build only) for <c>EXTENDSPACE_FREEZE_SECONDS</c> (10 by default) and
    /// continued. The host must notice the silence, reconnect within its budget once the app runs
    /// again, and the client decode a fresh keyframe. The process is always continued.
    /// </summary>
    [Fact]
    public async Task V19i_a_frozen_app_is_noticed_and_once_it_continues_the_session_recovers_with_a_fresh_keyframe()
    {
        int freezeSeconds = Seconds("EXTENDSPACE_FREEZE_SECONDS", 10);
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        string pid = await rig.AppPidAsync();
        long keyframesBefore = rig.Pipeline.Counters!.KeyframesDelivered;
        long continuedAt;
        try
        {
            await rig.FreezeAsync(pid);
            long frozenAt = Stopwatch.GetTimestamp();
            SessionState lost = await rig.WaitForAsync(s => s.Phase != SessionPhase.Streaming, TimeSpan.FromSeconds(30));
            _evidence.Line($"{lost.Phase} {Stopwatch.GetElapsedTime(frozenAt).TotalMilliseconds:0} ms after the freeze: {lost.LastAttemptFault?.Code}");
            Assert.Equal(SessionPhase.Reconnecting, lost.Phase);
            TimeSpan rest = TimeSpan.FromSeconds(freezeSeconds) - Stopwatch.GetElapsedTime(frozenAt);
            if (rest > TimeSpan.Zero)
            {
                await Task.Delay(rest, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await rig.ThawAsync(pid);
            continuedAt = Stopwatch.GetTimestamp();
        }

        SessionState back = await rig.WaitForAsync(s => s.Phase is SessionPhase.Streaming or SessionPhase.Failed, TimeSpan.FromSeconds(40));
        _evidence.Line($"{back.Phase} {Stopwatch.GetElapsedTime(continuedAt).TotalMilliseconds:0} ms after the app was continued; error {back.Error?.Cause?.Code ?? "none"}");
        Assert.Equal(SessionPhase.Streaming, back.Phase);
        long backAt = Stopwatch.GetTimestamp();
        await rig.UntilAsync(() => rig.Connection.LatestClientReport is { FramesRendered: > 0 } report && report.ReceivedMicros > MonotonicClockMicros(backAt), TimeSpan.FromSeconds(10), "nothing rendered after recovery");
        _evidence.Line($"after recovery: keyframes delivered {keyframesBefore} -> {rig.Pipeline.Counters!.KeyframesDelivered}; client {rig.Connection.LatestClientReport}");
        await rig.SampleAsync(3);
        await rig.StopAndCheckCleanAsync();
    }

    /// <summary>
    /// V19j (V7 with a frozen peer; V14's write to a frozen peer). The app is frozen while video
    /// flows; once the connection's queue starts dropping, its writer is stuck in a socket write
    /// nobody reads. Stop is then pressed: the session must reach Disconnected within the teardown
    /// deadline, and the time is recorded. If the host notices the silence first, the precondition
    /// was not reached and the test reports itself blocked.
    /// </summary>
    [Fact]
    public async Task V19j_stop_while_a_write_to_a_frozen_app_is_blocked_ends_the_session_within_the_teardown_deadline()
    {
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        string pid = await rig.AppPidAsync();
        try
        {
            long dropsBefore = rig.Connection.Counters.DroppedQueueFull;
            await rig.FreezeAsync(pid);
            long frozenAt = Stopwatch.GetTimestamp();
            while (rig.Connection.Counters.DroppedQueueFull < dropsBefore + 4 && rig.Coordinator.State.Phase == SessionPhase.Streaming && Stopwatch.GetElapsedTime(frozenAt) < TimeSpan.FromSeconds(4.5))
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            HostConnectionCounters stuck = rig.Connection.Counters;
            _evidence.Line($"{Stopwatch.GetElapsedTime(frozenAt).TotalMilliseconds:0} ms after the freeze: {rig.Coordinator.State.Phase}; queue-full drops +{stuck.DroppedQueueFull - dropsBefore}, transmitted {stuck.Transmitted}");
            Assert.SkipUnless(rig.Coordinator.State.Phase == SessionPhase.Streaming && stuck.DroppedQueueFull >= dropsBefore + 4, "BLOCKED: the writer was not seen stuck before the host noticed the silence");

            long stopAt = Stopwatch.GetTimestamp();
            Assert.True(rig.Coordinator.Stop());
            SessionState ended = await rig.WaitForAsync(s => s.Phase is SessionPhase.Disconnected or SessionPhase.Failed, TimeSpan.FromSeconds(15));
            double stopMs = Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds;
            _evidence.Line($"stop with a write blocked on the frozen app: {ended.Phase} after {stopMs:0} ms; teardown faults: {string.Join("; ", rig.Coordinator.LastTeardownFaults.Select(f => $"{f.Code}: {f.Message}"))}");
            Assert.Equal(SessionPhase.Disconnected, ended.Phase);
            Assert.True(stopMs < 10_000, $"stop took {stopMs:0} ms, past the 10 s teardown deadline");
        }
        finally
        {
            await rig.ThawAsync(pid);
        }

        await rig.UntilAsync(() => rig.Pipeline.IsReleased && rig.Coordinator.IsReleased, TimeSpan.FromSeconds(15), "not released");
        Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{rig.ForwardedPort} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// V19k (V14's blocked handshake read). The app is frozen; the host notices and reconnects: adb
    /// still accepts the connection (the frozen app's listening socket is there), so the host's read of
    /// the client's hello blocks for up to its 5 s step. One second into that read, Stop is pressed;
    /// the time to Disconnected is recorded. If the attempt ended before the stop (a refusal, not a
    /// blocked read), the precondition was not reached and the test reports itself blocked.
    /// </summary>
    [Fact]
    public async Task V19k_stop_during_a_handshake_read_blocked_on_a_frozen_app_ends_the_session_promptly()
    {
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        string pid = await rig.AppPidAsync();
        try
        {
            await rig.FreezeAsync(pid);
            await rig.WaitForAsync(s => s.Phase == SessionPhase.Reconnecting, TimeSpan.FromSeconds(30));
            int forwards = rig.ForwardsCreated;
            await rig.UntilAsync(() => rig.ForwardsCreated > forwards, TimeSpan.FromSeconds(10), "no reconnection attempt");
            int failuresAtAttempt = rig.HandshakeFailures;
            long attemptAt = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            // During an attempt the session is Negotiating; Reconnecting again would mean the attempt had already ended.
            Assert.SkipUnless(rig.HandshakeFailures == failuresAtAttempt && rig.Coordinator.State.Phase == SessionPhase.Negotiating, "BLOCKED: the attempt ended before the stop, so no read was blocked");

            long stopAt = Stopwatch.GetTimestamp();
            _evidence.Line($"stop {Stopwatch.GetElapsedTime(attemptAt, stopAt).TotalMilliseconds:0} ms after the attempt's forward was created, inside its 5 s wait for the client's hello");
            Assert.True(rig.Coordinator.Stop());
            SessionState ended = await rig.WaitForAsync(s => s.Phase is SessionPhase.Disconnected or SessionPhase.Failed, TimeSpan.FromSeconds(15));
            double stopMs = Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds;
            _evidence.Line($"stop during the blocked read: {ended.Phase} after {stopMs:0} ms; teardown faults: {string.Join("; ", rig.Coordinator.LastTeardownFaults.Select(f => $"{f.Code}: {f.Message}"))}");
            Assert.Equal(SessionPhase.Disconnected, ended.Phase);
            Assert.True(stopMs < 4_000, $"stop took {stopMs:0} ms: the blocked read was waited out rather than cancelled");
        }
        finally
        {
            await rig.ThawAsync(pid);
        }

        await rig.UntilAsync(() => rig.Pipeline.IsReleased && rig.Coordinator.IsReleased, TimeSpan.FromSeconds(15), "not released");
        Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{rig.ForwardedPort} ", StringComparison.Ordinal));
    }

    private static async Task StreamThenStopAsync(Rig rig, EncodingRequest expected)
    {
        await rig.StartStreamingAndRenderingAsync();
        Assert.StartsWith($"{expected.Width} x {expected.Height}, High profile", rig.Pipeline.ProducedStream, StringComparison.Ordinal);

        Rig.Summary summary = await rig.SampleAsync(StreamSeconds);
        Assert.True(summary.StreamingThroughout, "the session left Streaming while sampling");
        Assert.Equal(0, summary.ClientDecoderErrors);
        Assert.True(summary.EncodedPerSecond <= expected.FrameRate * 1.02, $"encoded {summary.EncodedPerSecond:0.00}/s, faster than the configured {expected.FrameRate}");

        // The client's report lags by up to a second, so allow two seconds of frames besides 5 %.
        Assert.True(summary.ClientRendered >= (summary.Sent * 0.95) - (2 * expected.FrameRate), $"the client rendered {summary.ClientRendered} of {summary.Sent} sent");
        await rig.StopAndCheckCleanAsync();
    }

    /// <summary>
    /// V19l (D61). A forced backlog mid-stream: the app's process is stopped for a moment (as in
    /// V19i, but far within the keepalive) and continued, so that what the host sent meanwhile
    /// arrives at once, faster than the decoder takes it. Twice, a few seconds apart. The session must
    /// stay Streaming and the client keep rendering; the client's log shows whether a picture was
    /// refused, the keyframe request and the recovery. <c>EXTENDSPACE_STALL_MILLIS</c> (300 by default)
    /// is how long the process is kept stopped beyond the two adb calls. Wants moving content.
    /// </summary>
    [Fact]
    public async Task V19l_a_short_stall_of_the_app_is_recovered_from_while_streaming()
    {
        int stallMillis = int.TryParse(Environment.GetEnvironmentVariable("EXTENDSPACE_STALL_MILLIS"), out int millis) && millis >= 0 ? millis : 300;
        using var rig = new Rig(_evidence);
        await rig.StartStreamingAndRenderingAsync();
        string pid = await rig.AppPidAsync();
        await rig.SampleAsync(3);

        for (int round = 1; round <= 2; round++)
        {
            long wantedBefore = rig.Connection.Counters.KeyframesWanted;
            long stoppedAt = Stopwatch.GetTimestamp();
            try
            {
                await rig.FreezeAsync(pid);
                await Task.Delay(stallMillis, TestContext.Current.CancellationToken);
            }
            finally
            {
                await rig.ThawAsync(pid);
            }

            _evidence.Line($"round {round}: the app was stopped for {Stopwatch.GetElapsedTime(stoppedAt).TotalMilliseconds:0} ms, adb calls included");
            Rig.Summary summary = await rig.SampleAsync(5);
            _evidence.Line($"round {round}: keyframes the host was asked for, passed on: {rig.Connection.Counters.KeyframesWanted - wantedBefore}; client {rig.Connection.LatestClientReport}");
            Assert.True(summary.StreamingThroughout, $"round {round}: the session left Streaming");
            Assert.Equal(0, summary.ClientDecoderErrors);
            Assert.True(summary.ClientRendered > 0, $"round {round}: nothing rendered after the stall");
        }

        await rig.StopAndCheckCleanAsync();
    }

    private static int Seconds(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int seconds) && seconds > 0 ? seconds : fallback;

    private static string Since(long? earlier, long later) =>
        earlier is { } at ? $"{Stopwatch.GetElapsedTime(at, later).TotalMilliseconds:0} ms" : "(not seen)";

    /// <summary>A <see cref="Stopwatch"/> timestamp on the connection's microsecond clock, which is the same counter.</summary>
    private static long MonotonicClockMicros(long timestamp) => (long)((Int128)timestamp * 1_000_000 / Stopwatch.Frequency);

    /// <summary>The production pieces, wired as <see cref="HostComposition"/> wires them, with evidence logging.</summary>
    private sealed class Rig : IDisposable
    {
        public const string Package = "com.rsxdevelops.extendspace";
        public const string SessionScreen = Package + "/.sessiontest.SessionDisplayActivity";

        private readonly EvidenceAdb _adb;
        private readonly Evidence _evidence;
        private readonly List<SessionPhase> _phases = [];
        private readonly string? _serial;
        private int _handshakeFailures;
        private (long At, long Transmitted) _lastKeyframeWanted;

        public Rig(Evidence evidence, SessionCoordinatorOptions? options = null)
        {
            _evidence = evidence;
            _adb = new EvidenceAdb(new AdbProcessRunner(), evidence);
            _serial = Environment.GetEnvironmentVariable(HostComposition.DeviceSerialVariable);
            evidence.Line($"adb: {new AdbProcessRunner().Executable ?? "not found"}; ANDROID_SERIAL is {(string.IsNullOrEmpty(_serial) ? "not set" : "set")}");
            Pipeline = new DesktopVideoPipeline(new DesktopVideoPipelineOptions { Log = line => evidence.Line($"pipeline: {line}") });
            Connection = new HostConnectionController(
                new UsbTransport(_adb, new UsbTransportOptions { DeviceSerial = _serial, Log = evidence.Line }),
                new HostConnectionOptions
                {
                    AppVersion = "stage4-acceptance",
                    Log = line =>
                    {
                        if (line.StartsWith("handshake failed", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _handshakeFailures);
                        }

                        evidence.Line(line);
                    },
                });
            Coordinator = new SessionCoordinator(new SessionStateMachine(), Connection, Pipeline, new SystemSessionClock(), options);

            // When a keyframe is asked for, and how many had been written by then: a forced keyframe
            // can be on the socket within milliseconds, sooner than a poll would see the request.
            Connection.KeyframeWanted += () =>
            {
                var wanted = (Stopwatch.GetTimestamp(), Connection.Counters.KeyframesTransmitted);
                lock (_phases)
                {
                    _lastKeyframeWanted = wanted;
                }
            };
            Coordinator.StateChanged += (_, _) =>
            {
                SessionState state = Coordinator.State;
                lock (_phases)
                {
                    _phases.Add(state.Phase);
                }

                evidence.Line($"state {state.Phase}{(state.LastAttemptFault is { } fault ? $" (last fault {fault.Code})" : string.Empty)}{(state.Error is { } error ? $" error {error.Code}/{error.Cause?.Code}: {error.Cause?.Message}" : string.Empty)}");
            };
        }

        /// <summary>One sampling period, summed.</summary>
        public sealed record Summary(bool StreamingThroughout, double Seconds, long Encoded, long Sent, long ClientRendered, long ClientDecoderErrors, double EncodedPerSecond);

        /// <summary>How the picture came back after the client's surface was lent again.</summary>
        public sealed record Resumption(double RequestedMs, double KeyframeSentMs, double RenderedMs);

        public DesktopVideoPipeline Pipeline { get; }

        public HostConnectionController Connection { get; }

        public SessionCoordinator Coordinator { get; }

        public int? ForwardedPort => _adb.LastForwardPort;

        /// <summary>State changes so far.</summary>
        public int PhaseChanges
        {
            get
            {
                lock (_phases)
                {
                    return _phases.Count;
                }
            }
        }

        public int AdbCommands => _adb.Commands;

        public int ForwardsCreated => _adb.ForwardsCreated;

        public int HandshakeFailures => Volatile.Read(ref _handshakeFailures);

        public SessionStartResult Start() => Coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror));

        public string RequireSerial()
        {
            Assert.SkipWhen(string.IsNullOrEmpty(_serial), "BLOCKED: set ANDROID_SERIAL to the tablet's serial for this test");
            return _serial!;
        }

        /// <summary>Start; Streaming; the first keyframe out of the encoder, onto the socket, and drawn by the client.</summary>
        public async Task StartStreamingAndRenderingAsync()
        {
            Stopwatch watch = Stopwatch.StartNew();
            Assert.Equal(SessionStartResult.Started, Start());
            SessionState state = await WaitForAsync(s => s.Phase is SessionPhase.Streaming or SessionPhase.Failed, TimeSpan.FromSeconds(30));
            Assert.True(state.Phase == SessionPhase.Streaming, $"ended {state.Phase}: {state.Error?.Cause?.Code} {state.Error?.Cause?.Message}");
            long streamingAt = watch.ElapsedMilliseconds;
            _evidence.Line($"Streaming {streamingAt} ms after start");
            foreach (string detail in Pipeline.Details)
            {
                _evidence.Line($"pipeline: {detail}");
            }

            await UntilAsync(() => Pipeline.Counters?.KeyframesDelivered >= 1 && Pipeline.ProducedStream is not null, TimeSpan.FromSeconds(5), "no checked keyframe left the encoder");
            VideoPipelineCounters first = Pipeline.Counters!;
            _evidence.Line($"first keyframe handed to the connection {first.FirstKeyframeAfterStart?.TotalMilliseconds:0.0} ms after the pipeline started (forced {first.KeyframesForced}, repeated {first.FramesRepeated}); encoder output: {Pipeline.ProducedStream}");
            await UntilAsync(() => Connection.Counters.KeyframesTransmitted >= 1, TimeSpan.FromSeconds(5), "no keyframe was written to the socket");
            _evidence.Line($"first keyframe written to the socket by {watch.ElapsedMilliseconds - streamingAt} ms after Streaming");
            await UntilAsync(() => Connection.LatestClientReport?.FramesRendered > 0, TimeSpan.FromSeconds(10), "the client reported no frame released to its surface");
            _evidence.Line($"client first reported a rendered frame by {watch.ElapsedMilliseconds - streamingAt} ms after Streaming: {Connection.LatestClientReport}");
        }

        /// <summary>Stop; Disconnected with no teardown fault; everything released; the forward removed.</summary>
        public async Task StopAndCheckCleanAsync()
        {
            long stopAt = Stopwatch.GetTimestamp();
            Assert.True(Coordinator.Stop());
            await WaitForAsync(s => s.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15));
            _evidence.Line($"stop to Disconnected: {Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds:0} ms; teardown faults: {Coordinator.LastTeardownFaults.Count}; pipeline counters at the end: {Pipeline.Counters}");
            Assert.Empty(Coordinator.LastTeardownFaults);
            Assert.True(Pipeline.IsReleased);
            Assert.True(Coordinator.IsReleased);
            Assert.DoesNotContain(await ForwardListAsync(), line => line.Contains($"tcp:{ForwardedPort} ", StringComparison.Ordinal));
        }

        /// <summary>
        /// The client's surface goes (<paramref name="away"/>) and is lent again (<paramref name="back"/>,
        /// or by itself when the screen is recreated). With a separate return, the absence is first seen:
        /// a whole report interval in which the client drew nothing. Then the picture must come back.
        /// </summary>
        public async Task SurfaceCycleAsync(string label, string[] away, string[]? back, TimeSpan awayFor)
        {
            ClientReport before = Connection.LatestClientReport!;
            long wanted = Connection.Counters.KeyframesWanted;
            _evidence.Line($"{label}: away");
            long awayAt = Stopwatch.GetTimestamp();
            await ShellAsync(away);
            if (back is not null)
            {
                ClientReport first = await NextReportAsync(MonotonicClockMicros(awayAt) + 200_000);
                ClientReport second = await NextReportAsync(first.ReceivedMicros);
                while (second.FramesRendered != first.FramesRendered)
                {
                    Assert.True(Stopwatch.GetElapsedTime(awayAt) < TimeSpan.FromSeconds(10), $"{label}: the client kept drawing");
                    (first, second) = (second, await NextReportAsync(second.ReceivedMicros));
                }

                _evidence.Line($"{label}: by {Stopwatch.GetElapsedTime(awayAt).TotalMilliseconds:0} ms nothing was drawn for a whole report interval; frames discarded meanwhile +{second.FramesDiscarded - before.FramesDiscarded}; session {Coordinator.State.Phase}");
                if (awayFor > TimeSpan.Zero)
                {
                    await Task.Delay(awayFor, TestContext.Current.CancellationToken);
                }

                _evidence.Line($"{label}: back");
                await ShellAsync(back);
            }

            await WaitForResumeAsync(label, wanted, TimeSpan.FromSeconds(10));
            Assert.Equal(before.DecoderErrors, Connection.LatestClientReport!.DecoderErrors);
            Assert.Equal(SessionPhase.Streaming, Coordinator.State.Phase);
        }

        /// <summary>
        /// Waits for the client to ask for a keyframe (its output lent again), for a keyframe to be
        /// written after that, and for a report after that showing a frame drawn. Null if
        /// <paramref name="giveUpWhen"/> becomes true first.
        /// </summary>
        public async Task<Resumption?> WaitForResumeAsync(string label, long wantedBefore, TimeSpan within, Func<bool>? giveUpWhen = null)
        {
            long from = Stopwatch.GetTimestamp();
            bool Waiting(Func<bool> done)
            {
                Assert.True(Stopwatch.GetElapsedTime(from) < within, $"{label}: the picture did not come back");
                return !done() && giveUpWhen?.Invoke() != true;
            }

            while (Waiting(() => Connection.Counters.KeyframesWanted > wantedBefore))
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            if (giveUpWhen?.Invoke() == true)
            {
                return null;
            }

            long requestedAt;
            long sent;
            lock (_phases)
            {
                (requestedAt, sent) = _lastKeyframeWanted;
            }

            long renderedThen = Connection.LatestClientReport?.FramesRendered ?? 0;
            while (Waiting(() => Connection.Counters.KeyframesTransmitted > sent))
            {
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }

            long sentAt = Stopwatch.GetTimestamp();
            while (Waiting(() => Connection.LatestClientReport is { } report && report.ReceivedMicros > MonotonicClockMicros(sentAt) && report.FramesRendered > renderedThen))
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            if (giveUpWhen?.Invoke() == true)
            {
                return null;
            }

            var resumed = new Resumption(
                Stopwatch.GetElapsedTime(from, requestedAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(from, sentAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(from).TotalMilliseconds);
            _evidence.Line($"{label}: client asked for a keyframe at {resumed.RequestedMs:0} ms, one was written {resumed.KeyframeSentMs - resumed.RequestedMs:0.0} ms after the request (polled every millisecond), and its next report showed a frame drawn by {resumed.RenderedMs:0} ms (reports come once a second); pipeline repeated {Pipeline.Counters?.FramesRepeated}; client {Connection.LatestClientReport}");
            return resumed;
        }

        public async Task<string> ShellAsync(params string[] command)
        {
            string[] arguments = string.IsNullOrEmpty(_serial) ? ["shell", .. command] : ["-s", _serial, "shell", .. command];
            AdbCommandResult result = await _adb.RunAsync(arguments, TimeSpan.FromSeconds(15), CancellationToken.None);
            Assert.True(result.Succeeded, $"adb shell {string.Join(' ', command)}: {result.Status} {result.ExitCode} {result.StandardError}");
            return result.StandardOutput;
        }

        public async Task<string> AppPidAsync()
        {
            string pid = (await ShellAsync("pidof", Package)).Trim();
            Assert.True(pid.Length > 0 && pid.All(char.IsAsciiDigit), $"the app is not running as one process: '{pid}'");
            return pid;
        }

        /// <summary>Stops the app's own process (debug build: <c>run-as</c>), and checks that it is stopped.</summary>
        public async Task FreezeAsync(string pid)
        {
            await ShellAsync("run-as", Package, "kill", "-STOP", pid);
            string state = await ProcessStateAsync(pid);
            _evidence.Line($"app process frozen: {state}");
            Assert.Contains("stopped", state, StringComparison.Ordinal);
        }

        public async Task ThawAsync(string pid)
        {
            await ShellAsync("run-as", Package, "kill", "-CONT", pid);
            _evidence.Line($"app process continued: {await ProcessStateAsync(pid)}");
        }

        /// <summary>Once a second for <paramref name="seconds"/>: rates on each side, drops, queue, client latency; every minute, the host process's memory.</summary>
        public async Task<Summary> SampleAsync(int seconds, bool stopWhenNotStreaming = false)
        {
            VideoPipelineCounters pipeline = Pipeline.Counters!;
            HostConnectionCounters connection = Connection.Counters;
            ClientReport? client = Connection.LatestClientReport;
            int phases = PhaseChanges;
            long at = Stopwatch.GetTimestamp();
            long firstAt = at;
            (VideoPipelineCounters Pipeline, HostConnectionCounters Connection, ClientReport? Client) start = (pipeline, connection, client);
            for (int second = 1; second <= seconds; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                VideoPipelineCounters p = Pipeline.Counters!;
                HostConnectionCounters c = Connection.Counters;
                ClientReport? r = Connection.LatestClientReport;
                double elapsed = Stopwatch.GetElapsedTime(at).TotalSeconds;
                at = Stopwatch.GetTimestamp();
                string clientPart = r is null || client is null
                    ? "client: no report"
                    : string.Create(CultureInfo.InvariantCulture, $"client received {r.FramesReceived - client.FramesReceived}, rendered {r.FramesRendered - client.FramesRendered}, missing {r.FramesMissing}, discarded {r.FramesDiscarded}, decoder errors {r.DecoderErrors}, render latency mean {r.RenderLatencyMeanMs:0.0} p95 {r.RenderLatencyP95Ms:0.0} ms, report age {(MonotonicClockMicros(at) - r.ReceivedMicros) / 1000} ms");
                _evidence.Line(string.Create(CultureInfo.InvariantCulture,
                    $"t+{second,3}s {Coordinator.State.Phase} [{Diagnose(Coordinator.State.Phase, pipeline, p, connection, c, client, r, MonotonicClockMicros(at))}]: captured {(p.FramesCaptured - pipeline.FramesCaptured) / elapsed:0.0}/s (callbacks {p.CaptureCallbacks - pipeline.CaptureCallbacks}, empty {p.CaptureEmptyCallbacks - pipeline.CaptureEmptyCallbacks}), encoded {(p.AccessUnitsDelivered - pipeline.AccessUnitsDelivered) / elapsed:0.0}/s, sent {(c.Transmitted - connection.Transmitted) / elapsed:0.0}/s at {(p.BytesDelivered - pipeline.BytesDelivered) * 8 / elapsed / 1e6:0.00} Mbit/s, host drops {c.DroppedQueueFull + c.DroppedAwaitingKeyframe - connection.DroppedQueueFull - connection.DroppedAwaitingKeyframe}, keyframes {p.KeyframesDelivered - pipeline.KeyframesDelivered}, repeated {p.FramesRepeated - pipeline.FramesRepeated}; {clientPart}; rtt {Connection.Clock?.RoundTripMicros / 1000.0:0.0} ms"));
                if (second % 60 == 0)
                {
                    using Process self = Process.GetCurrentProcess();
                    _evidence.Line(string.Create(CultureInfo.InvariantCulture,
                        $"host process: working set {self.WorkingSet64 / 1048576.0:0.0} MB, private {self.PrivateMemorySize64 / 1048576.0:0.0} MB, managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, handles {self.HandleCount}, threads {self.Threads.Count}"));
                }

                (pipeline, connection, client) = (p, c, r ?? client);
                if (stopWhenNotStreaming && Coordinator.State.Phase != SessionPhase.Streaming)
                {
                    break;
                }
            }

            VideoPipelineCounters endP = Pipeline.Counters!;
            HostConnectionCounters endC = Connection.Counters;
            ClientReport? endR = Connection.LatestClientReport;
            double total = Stopwatch.GetElapsedTime(firstAt).TotalSeconds;
            long encoded = endP.AccessUnitsDelivered - start.Pipeline.AccessUnitsDelivered;
            long sentFrames = endC.Transmitted - start.Connection.Transmitted;
            long rendered = (endR?.FramesRendered ?? 0) - (start.Client?.FramesRendered ?? 0);
            _evidence.Line(string.Create(CultureInfo.InvariantCulture,
                $"over {total:0.0} s: captured {(endP.FramesCaptured - start.Pipeline.FramesCaptured) / total:0.00}/s, encoded {encoded / total:0.00}/s, sent {sentFrames / total:0.00}/s, {(endP.BytesDelivered - start.Pipeline.BytesDelivered) * 8 / total / 1e6:0.00} Mbit/s; host drops queue-full {endC.DroppedQueueFull - start.Connection.DroppedQueueFull}, awaiting-keyframe {endC.DroppedAwaitingKeyframe - start.Connection.DroppedAwaitingKeyframe}; not encoded {endP.FramesNotEncoded - start.Pipeline.FramesNotEncoded}; client rendered {rendered} ({rendered / total:0.00}/s, its reports lag by up to 1 s), missing {endR?.FramesMissing}, decoder errors {endR?.DecoderErrors}"));
            return new Summary(PhaseChanges == phases && Coordinator.State.Phase == SessionPhase.Streaming, total, encoded, sentFrames, rendered, endR?.DecoderErrors ?? 0, encoded / total);
        }

        /// <summary>
        /// What one second's counters say, each condition from its own evidence (stage 4.1, G2). A
        /// second with no capture callbacks is a still screen or starved capture: the counters cannot
        /// tell which (section 19.7), so it is named as both. Nothing acts on this; it is evidence.
        /// </summary>
        public static string Diagnose(SessionPhase phase, VideoPipelineCounters p0, VideoPipelineCounters p1, HostConnectionCounters c0, HostConnectionCounters c1, ClientReport? r0, ClientReport? r1, long nowMicros)
        {
            var findings = new List<string>();
            if (phase != SessionPhase.Streaming)
            {
                findings.Add($"transport: {phase}");
            }

            if (p1.CaptureCallbacks == p0.CaptureCallbacks)
            {
                findings.Add("no capture callbacks: still screen or starved capture");
            }

            if (p1.FramesSubmitted > p0.FramesSubmitted && p1.AccessUnitsDelivered == p0.AccessUnitsDelivered)
            {
                findings.Add($"encoder stall: {p1.FramesSubmitted - p0.FramesSubmitted} submitted, none out");
            }

            long drops = c1.DroppedQueueFull + c1.DroppedAwaitingKeyframe - c0.DroppedQueueFull - c0.DroppedAwaitingKeyframe;
            if (drops > 0)
            {
                findings.Add($"send-queue drops: {drops}");
            }

            if (r1 is null || nowMicros - r1.ReceivedMicros > 2_500_000)
            {
                findings.Add("no client report for 2.5 s");
            }
            else if (r0 is not null && r1.FramesReceived > r0.FramesReceived && r1.FramesRendered == r0.FramesRendered)
            {
                findings.Add($"client drew nothing: {r1.FramesReceived - r0.FramesReceived} received, {r1.FramesDiscarded - r0.FramesDiscarded} discarded");
            }

            if (r0 is not null && r1 is not null && r1.DecoderErrors > r0.DecoderErrors)
            {
                findings.Add($"decoder errors: {r1.DecoderErrors - r0.DecoderErrors}");
            }

            return findings.Count == 0 ? "ok" : string.Join("; ", findings);
        }

        public async Task UntilAsync(Func<bool> condition, TimeSpan within, string failure)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(watch.Elapsed < within, failure);
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        public async Task<string[]> ForwardListAsync()
        {
            AdbCommandResult list = await new AdbProcessRunner().RunAsync(["forward", "--list"], TimeSpan.FromSeconds(10), CancellationToken.None);
            string[] lines = list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            _evidence.Line($"adb forward --list: {lines.Length} forward(s)");
            return lines;
        }

        public async Task<SessionState> WaitForAsync(Func<SessionState, bool> condition, TimeSpan within)
        {
            var reached = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check(object? sender, EventArgs e)
            {
                SessionState state = Coordinator.State;
                if (condition(state))
                {
                    reached.TrySetResult(state);
                }
            }

            Coordinator.StateChanged += Check;
            try
            {
                Check(null, EventArgs.Empty);
                return await reached.Task.WaitAsync(within);
            }
            finally
            {
                Coordinator.StateChanged -= Check;
            }
        }

        public void Dispose()
        {
            if (Coordinator.Stop())
            {
                WaitForAsync(state => state.Phase is SessionPhase.Disconnected or SessionPhase.Failed, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
        }

        private async Task<ClientReport> NextReportAsync(long afterMicros)
        {
            await UntilAsync(() => Connection.LatestClientReport?.ReceivedMicros > afterMicros, TimeSpan.FromSeconds(5), "the client sent no report for 5 s");
            return Connection.LatestClientReport!;
        }

        private async Task<string> ProcessStateAsync(string pid)
        {
            string status = await ShellAsync("run-as", Package, "cat", $"/proc/{pid}/status");
            return status.Split('\n').FirstOrDefault(line => line.StartsWith("State:", StringComparison.Ordinal))?.Trim() ?? "no state line";
        }
    }

    /// <summary>Polls <c>adb devices</c> every 200 ms and logs when the tablet leaves the list and comes back.</summary>
    private sealed class DeviceWatch : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private long _goneAt;
        private long _backAt;

        public DeviceWatch(Evidence evidence, string serial)
        {
            _loop = Task.Run(async () =>
            {
                var runner = new AdbProcessRunner();
                bool? listed = null;
                while (!_stop.IsCancellationRequested)
                {
                    AdbCommandResult devices = await runner.RunAsync(["devices"], TimeSpan.FromSeconds(5), CancellationToken.None);
                    bool now = devices.StandardOutput.Split('\n').Any(line => line.StartsWith(serial + "\t", StringComparison.Ordinal) && line.TrimEnd().EndsWith("\tdevice", StringComparison.Ordinal));
                    if (listed != now)
                    {
                        long timestamp = Stopwatch.GetTimestamp();
                        if (listed == true && !now)
                        {
                            Interlocked.CompareExchange(ref _goneAt, timestamp, 0);
                        }
                        else if (listed == false && now)
                        {
                            Interlocked.Exchange(ref _backAt, timestamp);
                        }

                        evidence.Line($"device watch: the tablet is {(now ? "listed as a device" : "not listed as a device")}");
                        listed = now;
                    }

                    try
                    {
                        await Task.Delay(200, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });
        }

        public long? GoneAt => Interlocked.Read(ref _goneAt) is var at and not 0 ? at : null;

        public long? BackAt => Interlocked.Read(ref _backAt) is var at and not 0 ? at : null;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }

    /// <summary>The real adb, each command, its duration and exit logged with the serial replaced.</summary>
    private sealed class EvidenceAdb(IAdbCommandRunner inner, Evidence evidence) : IAdbCommandRunner
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _commands;
        private int _forwardsCreated;

        public int? LastForwardPort { get; private set; }

        public int Commands => Volatile.Read(ref _commands);

        /// <summary>Forward commands that succeeded: one before every connection attempt.</summary>
        public int ForwardsCreated => Volatile.Read(ref _forwardsCreated);

        public async Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _commands);
            string shown = string.Join(' ', arguments.Select((argument, index) => index > 0 && arguments[index - 1] == "-s" ? "<device>" : argument));
            long started = _clock.ElapsedMilliseconds;
            AdbCommandResult result = await inner.RunAsync(arguments, timeout, cancellation);
            evidence.Line($"adb {shown}: {result.Status} {result.ExitCode} in {_clock.ElapsedMilliseconds - started} ms");
            if (arguments.Count >= 4 && arguments[2] == "forward" && arguments[3].StartsWith("tcp:", StringComparison.Ordinal))
            {
                LastForwardPort = int.Parse(arguments[3]["tcp:".Length..], CultureInfo.InvariantCulture);
                if (result.Succeeded)
                {
                    Interlocked.Increment(ref _forwardsCreated);
                }
            }

            return result;
        }
    }

    /// <summary>Timestamped lines to the test output and to a file under artifacts/device-acceptance.</summary>
    private sealed class Evidence : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly StreamWriter _file;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public Evidence(string test)
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "artifacts")))
            {
                root = root.Parent;
            }

            string directory = Path.Combine(root?.FullName ?? AppContext.BaseDirectory, "artifacts", "device-acceptance");
            Directory.CreateDirectory(directory);
            string name = new([.. test.Split('.')[^1].Where(c => char.IsLetterOrDigit(c) || c == '_')]);
            _file = new StreamWriter(Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name[..Math.Min(name.Length, 60)]}.log")) { AutoFlush = true };
            Line($"started {DateTimeOffset.Now:O} on {Environment.OSVersion}");
        }

        public void Line(string text)
        {
            string line = $"{_clock.ElapsedMilliseconds,8} ms  {text}";
            lock (_gate)
            {
                _file.WriteLine(line);
            }

            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        public void Dispose() => _file.Dispose();
    }
}
