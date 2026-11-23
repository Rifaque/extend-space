using System.Net;
using System.Net.Sockets;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Transport;

/// <summary>One line of <c>adb devices</c>.</summary>
/// <param name="State">adb's word for it: <c>device</c> when usable, otherwise <c>unauthorized</c>, <c>offline</c> and others.</param>
/// <param name="Model">The <c>model:</c> field of <c>adb devices -l</c>, underscores as spaces; null when adb did not give one (it does not for an unauthorised device).</param>
public sealed record AdbDevice(string Serial, string State, string? Model = null)
{
    public bool IsUsable => State == "device";

    public bool IsUnauthorized => State == "unauthorized";
}

public static class AdbDeviceList
{
    private const string Header = "List of devices attached";

    /// <summary>
    /// Reads the output of <c>adb devices</c>. Lines before the header (adb's own "daemon started"
    /// notes) and blank lines are skipped; a line after the header that is not "serial, whitespace,
    /// state" is refused rather than guessed at.
    /// </summary>
    /// <returns>Null when the output has no header: it is not a device list.</returns>
    public static IReadOnlyList<AdbDevice>? Parse(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int header = Array.IndexOf(lines, Header);
        if (header < 0)
        {
            return null;
        }

        var devices = new List<AdbDevice>();
        foreach (string line in lines[(header + 1)..])
        {
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || line.StartsWith('*'))
            {
                return null;
            }

            string? model = fields.Skip(2).FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..].Replace('_', ' ');
            devices.Add(new AdbDevice(fields[0], fields[1], string.IsNullOrWhiteSpace(model) ? null : model));
        }

        return devices;
    }
}

public sealed record UsbTransportOptions
{
    /// <summary>
    /// The device to use, as adb names it. Null or empty means "the one device attached": with none
    /// the attempt fails and may be retried, with several it fails for good, because guessing could
    /// mirror the screen to the wrong device (architecture section 12, stage 3). Surrounding
    /// whitespace is ignored; a value with whitespace or control characters inside cannot be an adb
    /// serial and fails the session rather than being ignored, since ignoring it would be a guess.
    /// The host application takes it from adb's own <c>ANDROID_SERIAL</c> variable (section 18.11).
    /// </summary>
    public string? DeviceSerial { get; init; }

    /// <summary>
    /// The device the user chose, read when a session's device is chosen (its first attempt, and
    /// each later one until an attempt reaches a device); takes precedence over
    /// <see cref="DeviceSerial"/> when it returns a value. The host application's Connect screen
    /// sets it (D65). The same rules apply to what it returns.
    /// </summary>
    public Func<string?>? DeviceSerialSource { get; init; }

    /// <summary>
    /// The bound on each adb command made while setting up: listing devices, adding the forward.
    /// The experiment's 4 s for restoring a forward (architecture section 15.1).
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>The bound on removing a forward, inside teardown (architecture section 5.2: about 2.5 s).</summary>
    public TimeSpan RemoveTimeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Lines for a diagnostic log. Device serials are never included.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The USB transport: adb, one chosen device, and one port forward from a loopback TCP port on this
/// PC to the app's local socket on the device (protocol section 3). Built from the experiment's
/// <c>AdbForward</c>, with device selection added and the port chosen per session.
///
/// <para><b>Ownership.</b> The forward belongs to the session from the moment the command that adds
/// it is started, not from when it succeeds: a command that is ended part-way may still have added
/// it. The port is recorded first, so <see cref="ReleaseAsync"/> always knows what to remove. A
/// call that finishes after a release (it was abandoned by the coordinator's teardown) removes what
/// it added itself. Each session gets a new port from the system, so a session can never reach a
/// forward left behind by an earlier one.</para>
///
/// <para><b>Faults.</b> adb missing or not startable, an unauthorised device, and several devices
/// with none chosen are terminal and the user can act on them; no device, a device not ready, adb
/// timing out and a forward refused are retryable, because a cable can be plugged in.</para>
/// </summary>
public sealed class UsbTransport(IAdbCommandRunner adb, UsbTransportOptions? options = null) : IHostTransport
{
    private readonly UsbTransportOptions _options = options ?? new UsbTransportOptions();
    private readonly Lock _gate = new();

    // Under _gate. One session's worth: cleared by ReleaseAsync.
    private int _generation;
    private string? _serial;
    private int? _port;
    private readonly HashSet<(string Serial, int Port)> _owned = [];

    /// <summary>Forwards this transport may have added and not yet removed. For tests and diagnostics.</summary>
    public int OwnedForwards
    {
        get
        {
            lock (_gate)
            {
                return _owned.Count;
            }
        }
    }

    public Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation) =>
        Task.Run(() => PrepareCoreAsync(cancellation), CancellationToken.None);

    public Task ReleaseAsync(CancellationToken cancellation) => Task.Run(ReleaseCoreAsync, CancellationToken.None);

    private async Task<AttemptOutcome<TransportEndpoint>> PrepareCoreAsync(CancellationToken cancellation)
    {
        string? chosen = _options.DeviceSerialSource?.Invoke();
        if (!TryReadConfiguredSerial(string.IsNullOrEmpty(chosen) ? _options.DeviceSerial : chosen, out string? configured))
        {
            return Failed(
                TransportFaultCodes.DeviceSerialInvalid,
                "The chosen Android device serial is not a valid adb serial. Choose the device again, or correct ANDROID_SERIAL or clear it.",
                FaultTreatment.Terminal,
                userActionable: true);
        }

        int generation;
        string? serial;
        lock (_gate)
        {
            generation = _generation;
            serial = _serial ?? configured;
        }

        AdbCommandResult listing = await adb.RunAsync(["devices"], _options.CommandTimeout, cancellation).ConfigureAwait(false);
        if (Problem(listing, "list the attached devices", TransportFaultCodes.AdbFailed) is { } listingProblem)
        {
            return listingProblem;
        }

        if (AdbDeviceList.Parse(listing.StandardOutput) is not { } devices)
        {
            Log("'adb devices' printed something that is not a device list");
            return Failed(TransportFaultCodes.AdbUnexpectedOutput, "adb did not list the attached devices in the expected form.", FaultTreatment.Retryable);
        }

        AttemptOutcome<TransportEndpoint>? choiceProblem = Choose(devices, ref serial);
        if (choiceProblem is not null)
        {
            return choiceProblem;
        }

        int port;
        lock (_gate)
        {
            if (_generation != generation)
            {
                return cancellation.IsCancellationRequested ? AttemptOutcome<TransportEndpoint>.Cancelled : Released();
            }

            // The device stays the session's, so that a different one plugged in later is never
            // picked up by a reconnection. The port is recorded as owned before the command that
            // may add its forward is started.
            _serial = serial;
            _port ??= FreeLoopbackPort();
            port = _port.Value;
            _owned.Add((serial!, port));
        }

        AdbCommandResult forward = await adb.RunAsync(
            ["-s", serial!, "forward", $"tcp:{port}", $"localabstract:{WireFormat.LocalSocketName}"],
            _options.CommandTimeout,
            cancellation).ConfigureAwait(false);

        bool releasedMeanwhile;
        lock (_gate)
        {
            releasedMeanwhile = _generation != generation;
            if (!releasedMeanwhile && forward.Status == AdbCommandStatus.Exited && forward.ExitCode != 0
                && Last(forward).Contains("cannot bind", StringComparison.OrdinalIgnoreCase))
            {
                // Another program took the port. The next attempt asks the system for a new one.
                // The pair stays owned; removing a forward that does not exist is harmless. Any
                // other refusal (the device went away) keeps the port, so that a session that
                // retries for its whole budget still owns one forward, not one per attempt.
                _port = null;
            }
        }

        if (releasedMeanwhile)
        {
            // This call outlived the session's release. Whatever it added is removed here, since
            // nobody else knows about it any more.
            await RemoveAsync(serial!, port).ConfigureAwait(false);
            return cancellation.IsCancellationRequested ? AttemptOutcome<TransportEndpoint>.Cancelled : Released();
        }

        if (Problem(forward, "forward a port to the device", TransportFaultCodes.ForwardFailed) is { } forwardProblem)
        {
            if (forward.Status == AdbCommandStatus.Exited)
            {
                Log($"'adb forward' failed with exit code {forward.ExitCode}: {Redact(Last(forward), serial)}");
            }

            return forwardProblem;
        }

        return AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(new IPEndPoint(IPAddress.Loopback, port)));
    }

    /// <summary>The configured serial, trimmed; null when none is configured. False when it cannot be one.</summary>
    internal static bool TryReadConfiguredSerial(string? value, out string? serial)
    {
        serial = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        serial = trimmed;
        return true;
    }

    private AttemptOutcome<TransportEndpoint>? Choose(IReadOnlyList<AdbDevice> devices, ref string? serial)
    {
        if (serial is not null)
        {
            string wanted = serial;
            AdbDevice? device = devices.FirstOrDefault(candidate => candidate.Serial == wanted);
            return device switch
            {
                null => Failed(TransportFaultCodes.DeviceNotAttached, "The Android device is not attached, or USB debugging is off.", FaultTreatment.Retryable),
                { IsUnauthorized: true } => Unauthorized(),
                { IsUsable: false } => Failed(TransportFaultCodes.DeviceNotReady, $"The Android device is attached but not ready ({device.State}).", FaultTreatment.Retryable),
                _ => null,
            };
        }

        List<AdbDevice> usable = [.. devices.Where(device => device.IsUsable)];
        switch (usable.Count)
        {
            case 1:
                serial = usable[0].Serial;
                Log($"using the one Android device attached ({devices.Count} listed)");
                return null;
            case 0 when devices.Any(device => device.IsUnauthorized):
                return Unauthorized();
            case 0:
                return Failed(TransportFaultCodes.DeviceNotAttached, "No Android device is attached with USB debugging on.", FaultTreatment.Retryable);
            default:
                return Failed(
                    TransportFaultCodes.SeveralDevices,
                    $"{usable.Count} Android devices are attached and none was chosen. Disconnect the others, or choose one.",
                    FaultTreatment.Terminal,
                    userActionable: true);
        }
    }

    private async Task ReleaseCoreAsync()
    {
        (string Serial, int Port)[] owned;
        lock (_gate)
        {
            _generation++;
            owned = [.. _owned];
            _owned.Clear();
            _serial = null;
            _port = null;
        }

        var failures = new List<string>();
        foreach ((string serial, int port) in owned)
        {
            if (await RemoveAsync(serial, port).ConfigureAwait(false) is { } failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"The adb port forward could not be removed: {string.Join("; ", failures)}");
        }
    }

    /// <summary>Removes one forward. Not cancellable: it is bounded instead. Null when it is gone.</summary>
    private async Task<string?> RemoveAsync(string serial, int port)
    {
        AdbCommandResult removed = await adb.RunAsync(["-s", serial, "forward", "--remove", $"tcp:{port}"], _options.RemoveTimeout, CancellationToken.None).ConfigureAwait(false);
        if (removed.Succeeded)
        {
            return null;
        }

        string said = Last(removed);
        if (removed.Status == AdbCommandStatus.Exited && said.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            // adb dropped the forward when the device went away, or the forward was never added.
            return null;
        }

        string failure = removed.Status switch
        {
            AdbCommandStatus.Exited => $"adb exited with {removed.ExitCode}: {Redact(said, serial)}",
            AdbCommandStatus.TimedOut => $"adb did not finish within {_options.RemoveTimeout.TotalSeconds:0.#} s",
            _ => $"adb could not be run ({removed.Status})",
        };
        Log($"removing the forward of port {port} failed: {failure}");
        return failure;
    }

    /// <summary>The outcome for a command that did not do what was asked; null when it did.</summary>
    private static AttemptOutcome<TransportEndpoint>? Problem(AdbCommandResult result, string what, string failureCode) => result.Status switch
    {
        AdbCommandStatus.Exited when result.ExitCode == 0 => null,
        AdbCommandStatus.Cancelled => AttemptOutcome<TransportEndpoint>.Cancelled,
        AdbCommandStatus.NotFound => Failed(
            TransportFaultCodes.AdbNotFound,
            "adb was not found. Install the Android SDK platform tools and set ANDROID_HOME, or put adb on PATH.",
            FaultTreatment.Terminal,
            userActionable: true),
        AdbCommandStatus.CouldNotStart => Failed(TransportFaultCodes.AdbNotStartable, "adb was found but could not be started.", FaultTreatment.Terminal, userActionable: true),
        AdbCommandStatus.TimedOut => Failed(TransportFaultCodes.AdbTimedOut, $"adb did not {what} in time.", FaultTreatment.Retryable),
        _ when IsUnauthorizedMessage(result) => Unauthorized(),

        // adb's words when the device went away between listing it and forwarding to it, or is
        // still coming up: retryable, and named for what happened rather than as a refusal.
        _ when Says(result, "not found") || Says(result, "no devices") => Failed(TransportFaultCodes.DeviceNotAttached, "The Android device went away while the connection was being set up.", FaultTreatment.Retryable),
        _ when Says(result, "offline") || Says(result, "authorizing") => Failed(TransportFaultCodes.DeviceNotReady, "The Android device is attached but not ready.", FaultTreatment.Retryable),
        _ => Failed(failureCode, $"adb could not {what} (exit code {result.ExitCode}).", FaultTreatment.Retryable),
    };

    private static bool IsUnauthorizedMessage(AdbCommandResult result) => Says(result, "unauthorized");

    private static bool Says(AdbCommandResult result, string words) => Last(result).Contains(words, StringComparison.OrdinalIgnoreCase);

    private static AttemptOutcome<TransportEndpoint> Unauthorized() => Failed(
        TransportFaultCodes.DeviceUnauthorized,
        "The Android device has not authorised this PC for USB debugging. Accept the prompt on the device and start again.",
        FaultTreatment.Terminal,
        userActionable: true);

    private static AttemptOutcome<TransportEndpoint> Released() =>
        Failed(TransportFaultCodes.TransportReleased, "The transport was released while it was being set up.", FaultTreatment.Retryable);

    private static AttemptOutcome<TransportEndpoint> Failed(string code, string message, FaultTreatment treatment, bool userActionable = false) =>
        AttemptOutcome<TransportEndpoint>.Failed(new SessionFault(FaultCategory.Transport, code, message, treatment, userActionable));

    /// <summary>
    /// A port the system says is free now. Another program could take it before adb binds it; adb then
    /// refuses, the attempt fails retryably, and the next attempt asks for another port.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static string Last(AdbCommandResult result)
    {
        string[] lines = $"{result.StandardOutput}\n{result.StandardError}".Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : string.Empty;
    }

    /// <summary>adb's messages name the device; the log does not (architecture section 9.2).</summary>
    private static string Redact(string text, string? serial) =>
        string.IsNullOrEmpty(serial) ? text : text.Replace(serial, "<device>", StringComparison.Ordinal);

    private void Log(string line) => _options.Log?.Invoke(line);
}

/// <summary>Fault codes the USB transport raises. All are <see cref="FaultCategory.Transport"/>.</summary>
public static class TransportFaultCodes
{
    public const string AdbNotFound = "adb_not_found";
    public const string AdbNotStartable = "adb_not_startable";
    public const string AdbTimedOut = "adb_timed_out";
    public const string AdbFailed = "adb_failed";
    public const string AdbUnexpectedOutput = "adb_unexpected_output";
    public const string DeviceNotAttached = "device_not_attached";
    public const string DeviceSerialInvalid = "device_serial_invalid";
    public const string DeviceNotReady = "device_not_ready";
    public const string DeviceUnauthorized = "device_unauthorized";
    public const string SeveralDevices = "several_devices_attached";
    public const string ForwardFailed = "forward_failed";
    public const string TransportReleased = "transport_released";
}
