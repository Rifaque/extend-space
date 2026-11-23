using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Transport;

/// <summary>
/// Device selection and forward ownership, against recorded <c>adb devices</c> output and a
/// scripted adb (architecture section 12, stage 3: "none, one, several, unauthorised"). No adb and
/// no device are involved.
/// </summary>
public sealed class UsbTransportTests
{
    private const string Tablet = "852ec620";
    private const string Emulator = "emulator-5554";

    private readonly ScriptedAdb _adb = new();
    private readonly List<string> _log = [];

    [Fact]
    public void The_device_list_is_read_past_adbs_own_notes_and_refused_when_it_is_not_a_list()
    {
        string output = "* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n852ec620\tdevice\nemulator-5554\toffline\nABC123\tunauthorized\n\n";

        Assert.Equal(
            [new AdbDevice(Tablet, "device"), new AdbDevice(Emulator, "offline"), new AdbDevice("ABC123", "unauthorized")],
            AdbDeviceList.Parse(output));
        Assert.Equal([], AdbDeviceList.Parse("List of devices attached\r\n\r\n"));
        Assert.Null(AdbDeviceList.Parse("error: protocol fault (couldn't read status): connection reset"));
        Assert.Null(AdbDeviceList.Parse("List of devices attached\nnot-a-device-line\n"));
    }

    [Fact]
    public async Task With_one_device_attached_it_is_chosen_and_a_loopback_port_is_forwarded_to_the_apps_socket()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();

        AttemptOutcome<TransportEndpoint> outcome = await transport.PrepareAsync(CancellationToken.None);

        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        int port = outcome.Value!.EndPoint.Port;
        Assert.Equal("127.0.0.1", outcome.Value.EndPoint.Address.ToString());
        Assert.Equal(["devices", $"-s {Tablet} forward tcp:{port} localabstract:extendspace"], _adb.Commands);
        Assert.Equal(1, transport.OwnedForwards);

        await transport.ReleaseAsync(CancellationToken.None);

        Assert.Equal($"-s {Tablet} forward --remove tcp:{port}", _adb.Commands[^1]);
        Assert.Equal(0, transport.OwnedForwards);
    }

    [Fact]
    public async Task With_no_device_the_attempt_fails_retryably_and_nothing_is_forwarded()
    {
        _adb.Devices = "List of devices attached\n\n";

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.DeviceNotAttached, true), (fault.Code, fault.IsRetryable));
        Assert.Equal(["devices"], _adb.Commands);
    }

    [Fact]
    public async Task With_several_devices_and_none_chosen_nothing_is_guessed_and_the_failure_is_terminal()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n{Emulator}\tdevice\n";

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((FaultCategory.Transport, TransportFaultCodes.SeveralDevices, false, true), (fault.Category, fault.Code, fault.IsRetryable, fault.UserActionable));
        Assert.Contains("2 Android devices", fault.Message);
        Assert.DoesNotContain(Tablet, fault.Message);
        Assert.DoesNotContain(Emulator, fault.Message);
        Assert.Equal(["devices"], _adb.Commands);
    }

    [Fact]
    public async Task A_chosen_device_is_used_among_several()
    {
        _adb.Devices = $"List of devices attached\n{Emulator}\tdevice\n{Tablet}\tdevice\n";

        AttemptOutcome<TransportEndpoint> outcome = await Create(Tablet).PrepareAsync(CancellationToken.None);

        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.StartsWith($"-s {Tablet} forward", _adb.Commands[^1]);
    }

    [Fact]
    public void The_long_device_list_gives_the_model_and_an_unauthorised_device_has_none()
    {
        string output = "List of devices attached\n852ec620               device usb:1-1 product:uke model:Xiaomi_Pad_7 device:uke transport_id:3\nABC123                 unauthorized usb:1-2 transport_id:4\n";

        Assert.Equal(
            [new AdbDevice(Tablet, "device", "Xiaomi Pad 7"), new AdbDevice("ABC123", "unauthorized")],
            AdbDeviceList.Parse(output));
    }

    [Fact]
    public async Task The_device_chosen_on_screen_is_used_among_several_and_wins_over_the_configured_one()
    {
        // D65: the Connect screen's choice, read when the session's device is chosen.
        _adb.Devices = $"List of devices attached\n{Emulator}\tdevice\n{Tablet}\tdevice\n";
        string? chosen = Tablet;
        var transport = new UsbTransport(_adb, new UsbTransportOptions { DeviceSerial = Emulator, DeviceSerialSource = () => chosen });

        AttemptOutcome<TransportEndpoint> outcome = await transport.PrepareAsync(CancellationToken.None);

        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.StartsWith($"-s {Tablet} forward", _adb.Commands[^1]);

        // Kept for the session: a different choice made meanwhile is not picked up by a reconnection.
        chosen = Emulator;
        await transport.PrepareAsync(CancellationToken.None);
        Assert.StartsWith($"-s {Tablet} forward", _adb.Commands[^1]);

        // A new session reads the choice again.
        await transport.ReleaseAsync(CancellationToken.None);
        await transport.PrepareAsync(CancellationToken.None);
        Assert.StartsWith($"-s {Emulator} forward", _adb.Commands[^1]);
    }

    [Fact]
    public async Task With_nothing_chosen_on_screen_the_configured_device_or_the_only_one_is_used()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        var transport = new UsbTransport(_adb, new UsbTransportOptions { DeviceSerialSource = () => null });

        Assert.Equal(OutcomeKind.Succeeded, (await transport.PrepareAsync(CancellationToken.None)).Kind);
        Assert.StartsWith($"-s {Tablet} forward", _adb.Commands[^1]);
    }

    [Fact]
    public async Task Discovery_lists_devices_with_their_models_and_says_what_is_wrong_with_adb()
    {
        _adb.Devices = "List of devices attached\n852ec620 device product:uke model:Xiaomi_Pad_7 transport_id:3\n";
        DeviceListResult listed = await new AdbDeviceDiscovery(_adb).ListAsync(CancellationToken.None);
        Assert.Equal(DeviceListStatus.Listed, listed.Status);
        Assert.Equal([new AdbDevice(Tablet, "device", "Xiaomi Pad 7")], listed.Devices);
        Assert.Equal("devices -l", _adb.Commands[^1]);

        _adb.Reply = (_, _) => new AdbCommandResult(AdbCommandStatus.NotFound);
        DeviceListResult missing = await new AdbDeviceDiscovery(_adb).ListAsync(CancellationToken.None);
        Assert.Equal(DeviceListStatus.AdbNotFound, missing.Status);
        Assert.Contains("adb was not found", missing.Problem);

        _adb.Reply = (_, _) => new AdbCommandResult(AdbCommandStatus.Exited, 1, "", "error");
        Assert.Equal(DeviceListStatus.AdbFailed, (await new AdbDeviceDiscovery(_adb).ListAsync(CancellationToken.None)).Status);

        _adb.Reply = (_, _) => new AdbCommandResult(AdbCommandStatus.TimedOut);
        Assert.Equal(DeviceListStatus.AdbFailed, (await new AdbDeviceDiscovery(_adb).ListAsync(CancellationToken.None)).Status);

        _adb.Reply = (_, _) => new AdbCommandResult(AdbCommandStatus.Exited, 0, "garbage");
        Assert.Equal(DeviceListStatus.AdbFailed, (await new AdbDeviceDiscovery(_adb).ListAsync(CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("unauthorized", TransportFaultCodes.DeviceUnauthorized, false)]
    [InlineData("offline", TransportFaultCodes.DeviceNotReady, true)]
    [InlineData("authorizing", TransportFaultCodes.DeviceNotReady, true)]
    public async Task A_chosen_device_that_cannot_be_used_yet_is_classified_by_its_state(string state, string code, bool retryable)
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\t{state}\n";

        SessionFault fault = await FailAsync(Create(Tablet));

        Assert.Equal((code, retryable), (fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task A_chosen_device_that_is_not_attached_is_a_retryable_failure()
    {
        _adb.Devices = $"List of devices attached\n{Emulator}\tdevice\n";

        SessionFault fault = await FailAsync(Create(Tablet));

        Assert.Equal((TransportFaultCodes.DeviceNotAttached, true), (fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task With_only_an_unauthorised_device_the_user_is_told_to_accept_the_prompt()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tunauthorized\n";

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.DeviceUnauthorized, false, true), (fault.Code, fault.IsRetryable, fault.UserActionable));
    }

    [Theory]
    [InlineData(AdbCommandStatus.NotFound, TransportFaultCodes.AdbNotFound, false)]
    [InlineData(AdbCommandStatus.CouldNotStart, TransportFaultCodes.AdbNotStartable, false)]
    [InlineData(AdbCommandStatus.TimedOut, TransportFaultCodes.AdbTimedOut, true)]
    public async Task Adb_that_is_missing_cannot_start_or_hangs_is_classified(AdbCommandStatus status, string code, bool retryable)
    {
        _adb.Reply = (_, _) => new AdbCommandResult(status);

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((FaultCategory.Transport, code, retryable), (fault.Category, fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task Output_that_is_not_a_device_list_is_a_retryable_failure_not_a_guess()
    {
        _adb.Devices = "adb server version (41) doesn't match this client (40); killing...";

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.AdbUnexpectedOutput, true), (fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task Later_attempts_restore_the_same_forward_to_the_same_device_even_when_another_appears()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();
        int port = (await transport.PrepareAsync(CancellationToken.None)).Value!.EndPoint.Port;

        // A second device appears; the session keeps the one it chose.
        _adb.Devices = $"List of devices attached\n{Emulator}\tdevice\n{Tablet}\tdevice\n";
        AttemptOutcome<TransportEndpoint> again = await transport.PrepareAsync(CancellationToken.None);

        Assert.Equal(port, again.Value!.EndPoint.Port);
        Assert.Equal($"-s {Tablet} forward tcp:{port} localabstract:extendspace", _adb.Commands[^1]);
        Assert.Equal(1, transport.OwnedForwards);
    }

    [Fact]
    public async Task While_the_device_is_away_every_attempt_fails_retryably_and_the_session_still_owns_one_forward()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();
        int port = (await transport.PrepareAsync(CancellationToken.None)).Value!.EndPoint.Port;

        _adb.Devices = "List of devices attached\n";
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(TransportFaultCodes.DeviceNotAttached, (await FailAsync(transport)).Code);
        }

        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        Assert.Equal(port, (await transport.PrepareAsync(CancellationToken.None)).Value!.EndPoint.Port);
        Assert.Equal(1, transport.OwnedForwards);
    }

    [Fact]
    public async Task A_device_that_goes_away_between_listing_and_forwarding_is_not_attached_and_logged_without_its_serial()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        _adb.Forward = _ => new AdbCommandResult(AdbCommandStatus.Exited, 1, StandardError: $"error: device '{Tablet}' not found");

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.DeviceNotAttached, true), (fault.Code, fault.IsRetryable));
        Assert.Contains(_log, line => line.Contains("device '<device>' not found", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, line => line.Contains(Tablet, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_that_goes_offline_between_listing_and_forwarding_is_not_ready()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        _adb.Forward = _ => new AdbCommandResult(AdbCommandStatus.Exited, 1, StandardError: "error: device offline");

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.DeviceNotReady, true), (fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task A_forward_adb_refuses_for_another_reason_is_a_retryable_forward_failure()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        _adb.Forward = _ => new AdbCommandResult(AdbCommandStatus.Exited, 1, StandardError: "error: closed");

        SessionFault fault = await FailAsync(Create());

        Assert.Equal((TransportFaultCodes.ForwardFailed, true), (fault.Code, fault.IsRetryable));
    }

    [Fact]
    public async Task The_configured_serial_has_surrounding_whitespace_ignored_and_names_the_device_in_every_command()
    {
        _adb.Devices = $"List of devices attached\n{Emulator}\tdevice\n{Tablet}\tdevice\n";
        UsbTransport transport = Create($"  {Tablet}\t");

        Assert.Equal(OutcomeKind.Succeeded, (await transport.PrepareAsync(CancellationToken.None)).Kind);
        await transport.PrepareAsync(CancellationToken.None);
        await transport.ReleaseAsync(CancellationToken.None);

        Assert.All(_adb.Commands.Where(command => command != "devices"), command => Assert.StartsWith($"-s {Tablet} forward", command));
        Assert.Equal(3, _adb.Commands.Count(command => command != "devices"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("852ec620 emulator-5554")]
    [InlineData("852ec620\nemulator-5554")]
    public async Task A_configured_serial_that_cannot_be_an_adb_serial_fails_the_session_before_adb_is_run(string serial)
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";

        SessionFault fault = await FailAsync(Create(serial));

        Assert.Equal((TransportFaultCodes.DeviceSerialInvalid, false, true), (fault.Code, fault.IsRetryable, fault.UserActionable));
        Assert.Empty(_adb.Commands);
    }

    [Fact]
    public async Task An_empty_configured_serial_means_none_was_configured()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";

        Assert.Equal(OutcomeKind.Succeeded, (await Create(string.Empty).PrepareAsync(CancellationToken.None)).Kind);
        Assert.StartsWith($"-s {Tablet} forward", _adb.Commands[^1]);
    }

    [Fact]
    public async Task A_port_another_program_took_is_given_up_and_the_next_attempt_asks_for_a_new_one()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        int? refused = null;
        _adb.Forward = arguments =>
        {
            if (refused is null)
            {
                refused = int.Parse(arguments[3]["tcp:".Length..]);
                return new AdbCommandResult(AdbCommandStatus.Exited, 1, StandardError: "error: cannot bind listener: Address already in use");
            }

            return new AdbCommandResult(AdbCommandStatus.Exited, 0);
        };
        UsbTransport transport = Create();

        await FailAsync(transport);
        AttemptOutcome<TransportEndpoint> second = await transport.PrepareAsync(CancellationToken.None);

        Assert.NotEqual(refused, second.Value!.EndPoint.Port);
    }

    [Fact]
    public async Task A_forward_command_ended_by_cancellation_may_have_added_the_forward_so_the_release_removes_it()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        _adb.Forward = _ => new AdbCommandResult(AdbCommandStatus.Cancelled);
        UsbTransport transport = Create();

        AttemptOutcome<TransportEndpoint> outcome = await transport.PrepareAsync(CancellationToken.None);
        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(1, transport.OwnedForwards);
        int port = int.Parse(_adb.Commands[^1].Split(' ')[3]["tcp:".Length..]);

        await transport.ReleaseAsync(CancellationToken.None);

        Assert.Equal($"-s {Tablet} forward --remove tcp:{port}", _adb.Commands[^1]);
    }

    [Fact]
    public async Task A_prepare_that_finishes_after_the_release_removes_the_forward_it_added()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        var forwardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _adb.ForwardAsync = async _ =>
        {
            forwardStarted.SetResult();
            await forwardMayFinish.Task;
            return new AdbCommandResult(AdbCommandStatus.Exited, 0);
        };
        UsbTransport transport = Create();

        // The coordinator abandoned this call at its deadline; the release runs while adb is still
        // adding the forward, and finds it already recorded.
        Task<AttemptOutcome<TransportEndpoint>> prepare = transport.PrepareAsync(CancellationToken.None);
        await forwardStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await transport.ReleaseAsync(CancellationToken.None);
        int removalsAfterRelease = _adb.Commands.Count(command => command.Contains("--remove", StringComparison.Ordinal));
        forwardMayFinish.SetResult();
        AttemptOutcome<TransportEndpoint> outcome = await prepare.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal((OutcomeKind.Failed, TransportFaultCodes.TransportReleased), (outcome.Kind, outcome.Fault!.Code));
        Assert.Equal(removalsAfterRelease + 1, _adb.Commands.Count(command => command.Contains("--remove", StringComparison.Ordinal)));
        Assert.Equal(0, transport.OwnedForwards);
    }

    [Fact]
    public async Task Releasing_ignores_cancellation_because_stopping_early_would_leave_the_forward_behind()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();
        await transport.PrepareAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await transport.ReleaseAsync(cancelled.Token);

        Assert.Contains("--remove", _adb.Commands[^1]);
        Assert.False(_adb.Tokens[^1].CanBeCanceled);
        Assert.Equal(TimeSpan.FromSeconds(2.5), _adb.Timeouts[^1]);
    }

    [Fact]
    public async Task A_forward_that_is_already_gone_is_not_a_cleanup_failure_and_one_that_cannot_be_removed_is()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();
        await transport.PrepareAsync(CancellationToken.None);
        _adb.Remove = _ => new AdbCommandResult(AdbCommandStatus.Exited, 1, StandardError: "error: listener 'tcp:1234' not found");
        await transport.ReleaseAsync(CancellationToken.None);

        await transport.PrepareAsync(CancellationToken.None);
        _adb.Remove = _ => new AdbCommandResult(AdbCommandStatus.TimedOut);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ReleaseAsync(CancellationToken.None));

        Assert.Contains("did not finish", failure.Message);
        Assert.Equal(0, transport.OwnedForwards);
    }

    [Fact]
    public async Task A_new_session_after_a_release_chooses_its_device_again()
    {
        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n";
        UsbTransport transport = Create();
        await transport.PrepareAsync(CancellationToken.None);
        await transport.ReleaseAsync(CancellationToken.None);

        _adb.Devices = $"List of devices attached\n{Tablet}\tdevice\n{Emulator}\tdevice\n";

        Assert.Equal(TransportFaultCodes.SeveralDevices, (await FailAsync(transport)).Code);
    }

    [Fact]
    public async Task A_cancelled_listing_is_reported_as_cancelled_and_owns_nothing()
    {
        _adb.Reply = (_, _) => new AdbCommandResult(AdbCommandStatus.Cancelled);
        UsbTransport transport = Create();

        Assert.Equal(OutcomeKind.Cancelled, (await transport.PrepareAsync(CancellationToken.None)).Kind);
        Assert.Equal(0, transport.OwnedForwards);
    }

    private UsbTransport Create(string? serial = null) => new(_adb, new UsbTransportOptions
    {
        DeviceSerial = serial,
        Log = line =>
        {
            lock (_log)
            {
                _log.Add(line);
            }
        },
    });

    private static async Task<SessionFault> FailAsync(UsbTransport transport)
    {
        AttemptOutcome<TransportEndpoint> outcome = await transport.PrepareAsync(CancellationToken.None);
        Assert.Equal(OutcomeKind.Failed, outcome.Kind);
        return outcome.Fault!;
    }

    /// <summary>adb as a script: records every command, its time limit and its token.</summary>
    private sealed class ScriptedAdb : IAdbCommandRunner
    {
        private readonly Lock _gate = new();

        public string Devices { get; set; } = "List of devices attached\n";

        /// <summary>Replaces every reply when set.</summary>
        public Func<IReadOnlyList<string>, CancellationToken, AdbCommandResult>? Reply { get; set; }

        public Func<IReadOnlyList<string>, AdbCommandResult> Forward { get; set; } = _ => new AdbCommandResult(AdbCommandStatus.Exited, 0);

        public Func<IReadOnlyList<string>, Task<AdbCommandResult>>? ForwardAsync { get; set; }

        public Func<IReadOnlyList<string>, AdbCommandResult> Remove { get; set; } = _ => new AdbCommandResult(AdbCommandStatus.Exited, 0);

        public List<string> Commands { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
        {
            lock (_gate)
            {
                Commands.Add(string.Join(' ', arguments));
                Timeouts.Add(timeout);
                Tokens.Add(cancellation);
            }

            if (Reply is not null)
            {
                return Task.FromResult(Reply(arguments, cancellation));
            }

            if (arguments is ["devices"] or ["devices", "-l"])
            {
                return Task.FromResult(new AdbCommandResult(AdbCommandStatus.Exited, 0, Devices));
            }

            if (arguments.Contains("--remove"))
            {
                return Task.FromResult(Remove(arguments));
            }

            return ForwardAsync?.Invoke(arguments) ?? Task.FromResult(Forward(arguments));
        }
    }
}
