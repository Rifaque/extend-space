using System.Diagnostics;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Device;

/// <summary>
/// Stage 3 acceptance on hardware (device tests V16 and V17, architecture section 18.12): the
/// production coordinator, connection controller, USB transport and adb runner, against a real
/// device through the real adb. The pipeline is the scripted stand-in, because capture and encoding
/// are stage 4: a session negotiates and streams pings, and **no video is sent**. That is the
/// expected stage 3 limitation, not a streaming pass.
///
/// Skipped unless <c>EXTENDSPACE_DEVICE_TESTS=1</c>. Each test is run on its own, with the device
/// in the state its summary names, and writes an evidence log to
/// <c>artifacts/device-acceptance/</c> (git-ignored). The device is chosen as the host application
/// chooses it: <c>ANDROID_SERIAL</c> if set, otherwise the only device attached.
/// </summary>
[Trait("Category", "Device")]
public sealed class DeviceAcceptanceTests : IDisposable
{
    private readonly Evidence _evidence;

    public DeviceAcceptanceTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_DEVICE_TESTS") == "1", "device acceptance: set EXTENDSPACE_DEVICE_TESTS=1 with a device attached (architecture section 18.12)");
        _evidence = new Evidence(TestContext.Current.Test?.TestDisplayName ?? "device-test");
    }

    public void Dispose() => _evidence?.Dispose();

    /// <summary>V16 (a). Needs: the debug app's live screen open on the device, listening.</summary>
    [Fact]
    public async Task V16a_a_session_selects_the_device_forwards_negotiates_streams_and_removes_its_forward()
    {
        using var rig = new Rig(_evidence);
        Stopwatch watch = Stopwatch.StartNew();

        Assert.Equal(SessionStartResult.Started, rig.Start());
        SessionState streaming = await rig.WaitForAsync(state => state.Phase is SessionPhase.Streaming or SessionPhase.Failed, TimeSpan.FromSeconds(30));
        _evidence.Line($"reached {streaming.Phase} after {watch.ElapsedMilliseconds} ms; error: {streaming.Error?.Cause?.Code ?? "none"}");
        Assert.Equal(SessionPhase.Streaming, streaming.Phase);
        int port = rig.ForwardedPort ?? throw new InvalidOperationException("no forward command was seen");
        Assert.Contains(await rig.ForwardListAsync(), line => line.Contains($"tcp:{port} localabstract:extendspace", StringComparison.Ordinal));

        // A few seconds of keepalive on the real link.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(SessionPhase.Streaming, rig.Coordinator.State.Phase);
        _evidence.Line($"clock estimate: {rig.Connection.Clock?.ToString() ?? "none"}");

        long stopAt = watch.ElapsedMilliseconds;
        Assert.True(rig.Coordinator.Stop());
        await rig.WaitForAsync(state => state.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15));
        _evidence.Line($"stop to Disconnected: {watch.ElapsedMilliseconds - stopAt} ms; teardown faults: {rig.Coordinator.LastTeardownFaults.Count}");

        Assert.True(rig.Coordinator.IsReleased);
        Assert.Empty(rig.Coordinator.LastTeardownFaults);
        Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{port} ", StringComparison.Ordinal));
    }

    /// <summary>V16 (b). Needs: the live screen open. Ten sessions in a row; nothing may be left behind.</summary>
    [Fact]
    public async Task V16b_repeated_sessions_leave_no_forward_or_connection_behind()
    {
        using var rig = new Rig(_evidence);
        var ports = new List<int>();
        for (int round = 0; round < 10; round++)
        {
            Assert.Equal(SessionStartResult.Started, rig.Start());
            await rig.WaitForAsync(state => state.Phase == SessionPhase.Streaming, TimeSpan.FromSeconds(30));
            ports.Add(rig.ForwardedPort!.Value);
            Assert.True(rig.Coordinator.Stop());
            await rig.WaitForAsync(state => state.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15));
            Assert.True(rig.Coordinator.IsReleased, $"round {round}");
            Assert.False(rig.Connection.HasConnection, $"round {round}");
        }

        string[] forwards = await rig.ForwardListAsync();
        _evidence.Line($"ports used: {string.Join(", ", ports)}");
        Assert.DoesNotContain(forwards, line => ports.Any(port => line.Contains($"tcp:{port} ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V16 (c), V14 in part. Needs: the device attached, the app **not** listening (live screen
    /// closed). The session retries no_app_listening; it is stopped while it waits.
    /// </summary>
    [Fact]
    public async Task V16c_stopping_while_waiting_for_the_app_is_prompt_and_removes_the_forward()
    {
        using var rig = new Rig(_evidence);
        Assert.Equal(SessionStartResult.Started, rig.Start());
        SessionState trying = await rig.WaitForAsync(state => state.LastAttemptFault is not null || state.Phase == SessionPhase.Streaming, TimeSpan.FromSeconds(20));
        Assert.True(trying.Phase != SessionPhase.Streaming, "the app was listening; close its live screen for this test");
        _evidence.Line($"retrying because: {trying.LastAttemptFault!.Code}");

        long stoppedAt = Stopwatch.GetTimestamp();
        Assert.True(rig.Coordinator.Stop());
        await rig.WaitForAsync(state => state.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15));
        _evidence.Line($"stop to Disconnected: {Stopwatch.GetElapsedTime(stoppedAt).TotalMilliseconds:0} ms; forward removal finished {rig.RemovalFinishedAfterMs(stoppedAt)} ms after the stop");
        Assert.True(rig.Coordinator.IsReleased);
        Assert.DoesNotContain(await rig.ForwardListAsync(), line => line.Contains($"tcp:{rig.ForwardedPort} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// V16 (d), V11. Needs: the live screen open, and an operator who pulls the USB cable within
    /// 20 s of the log line "pull the cable now" and plugs it back within the 30 s budget.
    /// </summary>
    [Fact]
    public async Task V16d_a_cable_pull_while_streaming_reconnects_with_the_forward_restored()
    {
        using var rig = new Rig(_evidence);
        Assert.Equal(SessionStartResult.Started, rig.Start());
        await rig.WaitForAsync(state => state.Phase == SessionPhase.Streaming, TimeSpan.FromSeconds(30));
        _evidence.Line("pull the cable now");
        Console.WriteLine("V16d: pull the USB cable now, then plug it back in.");

        Stopwatch watch = Stopwatch.StartNew();
        SessionState lost = await rig.WaitForAsync(state => state.Phase == SessionPhase.Reconnecting, TimeSpan.FromSeconds(20));
        _evidence.Line($"loss seen after {watch.ElapsedMilliseconds} ms: {lost.LastAttemptFault?.Code}");
        SessionState back = await rig.WaitForAsync(state => state.Phase is SessionPhase.Streaming or SessionPhase.Failed, TimeSpan.FromSeconds(60));
        _evidence.Line($"{back.Phase} {watch.ElapsedMilliseconds} ms after the prompt; error: {back.Error?.Cause?.Code ?? "none"}");
        Assert.Equal(SessionPhase.Streaming, back.Phase);
        Assert.True(rig.Prepares >= 2, "the forward was not set up again");
    }

    /// <summary>
    /// V17. Needs: two usable devices attached (for example the tablet and an emulator) and
    /// ANDROID_SERIAL naming the tablet.
    /// </summary>
    [Fact]
    public async Task V17_with_two_devices_none_is_guessed_and_the_named_one_is_used()
    {
        string serial = Environment.GetEnvironmentVariable(HostComposition.DeviceSerialVariable)
            ?? throw new InvalidOperationException("set ANDROID_SERIAL to the tablet's serial for this test");
        var adb = new EvidenceAdb(new AdbProcessRunner(), _evidence);

        // The prerequisite is checked first, so that a missing second device is reported as such
        // (BLOCKED) rather than as the product failing to refuse a guess it never had to make.
        AdbCommandResult listing = await adb.RunAsync(["devices"], TimeSpan.FromSeconds(10), CancellationToken.None);
        AdbDevice[] usable = [.. (AdbDeviceList.Parse(listing.StandardOutput) ?? []).Where(device => device.IsUsable)];
        _evidence.Line($"usable devices: {usable.Length}; the named one among them: {usable.Any(device => device.Serial == serial)}");
        Assert.SkipUnless(
            usable.Length >= 2 && usable.Any(device => device.Serial == serial),
            $"BLOCKED: V17 needs two usable devices, one of them ANDROID_SERIAL; {usable.Length} usable attached");

        var unnamed = new UsbTransport(adb, new UsbTransportOptions { Log = _evidence.Line });
        var named = new UsbTransport(adb, new UsbTransportOptions { DeviceSerial = serial, Log = _evidence.Line });
        try
        {
            AttemptOutcome<TransportEndpoint> refused = await unnamed.PrepareAsync(CancellationToken.None);
            Assert.Equal(TransportFaultCodes.SeveralDevices, refused.Fault?.Code);
            Assert.DoesNotContain(adb.Commands, command => command.Contains(" forward tcp:", StringComparison.Ordinal));

            AttemptOutcome<TransportEndpoint> accepted = await named.PrepareAsync(CancellationToken.None);
            Assert.Equal(OutcomeKind.Succeeded, accepted.Kind);
            Assert.Contains(adb.Commands, command => command.StartsWith($"-s {serial} forward tcp:", StringComparison.Ordinal));
        }
        finally
        {
            // Whatever an assertion found, nothing this test forwarded is left behind.
            await unnamed.ReleaseAsync(CancellationToken.None);
            await named.ReleaseAsync(CancellationToken.None);
        }

        Assert.Equal(0, named.OwnedForwards);
    }

    /// <summary>The production pieces, wired as <see cref="HostComposition"/> wires them, with the stand-in pipeline.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly EvidenceAdb _adb;
        private readonly Evidence _evidence;

        public Rig(Evidence evidence)
        {
            _evidence = evidence;
            _adb = new EvidenceAdb(new AdbProcessRunner(), evidence);
            string? serial = Environment.GetEnvironmentVariable(HostComposition.DeviceSerialVariable);
            evidence.Line($"adb: {new AdbProcessRunner().Executable ?? "not found"}; ANDROID_SERIAL is {(string.IsNullOrEmpty(serial) ? "not set" : "set")}");
            Connection = new HostConnectionController(
                new UsbTransport(_adb, new UsbTransportOptions { DeviceSerial = serial, Log = evidence.Line }),
                new HostConnectionOptions { AppVersion = "stage3-acceptance", Log = evidence.Line });
            Coordinator = new SessionCoordinator(new SessionStateMachine(), Connection, new FakePipeline(new CallLog()), new SystemSessionClock());
            Coordinator.StateChanged += (_, _) =>
            {
                SessionState state = Coordinator.State;
                evidence.Line($"state {state.Phase}{(state.LastAttemptFault is { } fault ? $" (last fault {fault.Code})" : string.Empty)}{(state.Error is { } error ? $" error {error.Code}/{error.Cause?.Code}" : string.Empty)}");
            };
        }

        public HostConnectionController Connection { get; }

        public SessionCoordinator Coordinator { get; }

        public int? ForwardedPort => _adb.LastForwardPort;

        public int Prepares => _adb.Commands.Count(command => command == "devices");

        public SessionStartResult Start() => Coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror));

        public async Task<string[]> ForwardListAsync()
        {
            AdbCommandResult list = await new AdbProcessRunner().RunAsync(["forward", "--list"], TimeSpan.FromSeconds(10), CancellationToken.None);
            string[] lines = list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            _evidence.Line($"adb forward --list: {lines.Length} forward(s)");
            return lines;
        }

        /// <summary>Milliseconds from <paramref name="since"/> (a Stopwatch timestamp) to the last forward removal finishing; -1 if none did.</summary>
        public long RemovalFinishedAfterMs(long since) => _adb.LastRemovalFinished is { } at ? (long)Stopwatch.GetElapsedTime(since, at).TotalMilliseconds : -1;

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
                WaitForAsync(state => state.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>The real adb, with every command, its duration and its exit written to the evidence log.</summary>
    private sealed class EvidenceAdb(IAdbCommandRunner inner, Evidence evidence) : IAdbCommandRunner
    {
        private readonly Lock _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public List<string> Commands { get; } = [];

        public int? LastForwardPort { get; private set; }

        /// <summary>A Stopwatch timestamp.</summary>
        public long? LastRemovalFinished { get; private set; }

        public async Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
        {
            // The serial is replaced in the log; the evidence is shareable (architecture section 9.2).
            string shown = string.Join(' ', arguments.Select((argument, index) => index > 0 && arguments[index - 1] == "-s" ? "<device>" : argument));
            lock (_gate)
            {
                Commands.Add(string.Join(' ', arguments));
            }

            long started = _clock.ElapsedMilliseconds;
            AdbCommandResult result = await inner.RunAsync(arguments, timeout, cancellation);
            evidence.Line($"adb {shown}: {result.Status} {result.ExitCode} in {_clock.ElapsedMilliseconds - started} ms");
            if (arguments.Count >= 4 && arguments[2] == "forward" && arguments[3].StartsWith("tcp:", StringComparison.Ordinal))
            {
                LastForwardPort = int.Parse(arguments[3]["tcp:".Length..]);
            }

            if (arguments.Contains("--remove"))
            {
                LastRemovalFinished = Stopwatch.GetTimestamp();
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
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SharedTestData.Find("tests/session-faults/error-mapping.tsv"))!, "..", ".."));
            string directory = Path.Combine(root, "artifacts", "device-acceptance");
            Directory.CreateDirectory(directory);
            string name = new([.. test.Split('.')[^1].Where(c => char.IsLetterOrDigit(c) || c == '_')]);
            _file = new StreamWriter(Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.log")) { AutoFlush = true };
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
