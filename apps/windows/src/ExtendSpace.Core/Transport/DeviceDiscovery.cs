namespace ExtendSpace.Core.Transport;

public enum DeviceListStatus
{
    /// <summary>adb answered with a device list (possibly empty).</summary>
    Listed,

    /// <summary>No adb executable was found.</summary>
    AdbNotFound,

    /// <summary>adb was found but could not list devices: it failed, timed out or printed something unexpected.</summary>
    AdbFailed,
}

/// <param name="Problem">For a status other than <see cref="DeviceListStatus.Listed"/>, what to tell the user.</param>
public sealed record DeviceListResult(DeviceListStatus Status, IReadOnlyList<AdbDevice> Devices, string? Problem = null)
{
    public static DeviceListResult Failed(DeviceListStatus status, string problem) => new(status, [], problem);
}

/// <summary>Lists the Android devices the user can choose from (D65). For the Connect screen only.</summary>
public interface IDeviceDiscovery
{
    Task<DeviceListResult> ListAsync(CancellationToken cancellation);
}

/// <summary>
/// <c>adb devices -l</c>, read with the transport's own parser. It never chooses a device and has no
/// effect on a session: the transport checks the chosen device again when a session starts, because
/// the list can be out of date by then.
/// </summary>
public sealed class AdbDeviceDiscovery(IAdbCommandRunner adb, TimeSpan? timeout = null) : IDeviceDiscovery
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(4);

    public async Task<DeviceListResult> ListAsync(CancellationToken cancellation)
    {
        AdbCommandResult result = await adb.RunAsync(["devices", "-l"], _timeout, cancellation).ConfigureAwait(false);
        switch (result.Status)
        {
            case AdbCommandStatus.NotFound:
                return DeviceListResult.Failed(
                    DeviceListStatus.AdbNotFound,
                    "adb was not found. Install the Android SDK Platform-Tools and set ANDROID_HOME, or put adb on PATH.");
            case AdbCommandStatus.CouldNotStart:
                return DeviceListResult.Failed(DeviceListStatus.AdbFailed, "adb was found but could not be started.");
            case AdbCommandStatus.TimedOut:
                return DeviceListResult.Failed(DeviceListStatus.AdbFailed, "adb did not answer in time.");
            case AdbCommandStatus.Cancelled:
                cancellation.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellation);
        }

        if (!result.Succeeded)
        {
            return DeviceListResult.Failed(DeviceListStatus.AdbFailed, $"adb could not list the attached devices (exit code {result.ExitCode}).");
        }

        return AdbDeviceList.Parse(result.StandardOutput) is { } devices
            ? new DeviceListResult(DeviceListStatus.Listed, devices)
            : DeviceListResult.Failed(DeviceListStatus.AdbFailed, "adb did not list the attached devices in the expected form.");
    }

    /// <summary>
    /// One line about adb for the Diagnostics page and support: its version and Platform-Tools
    /// revision, or why it cannot be used. Never throws for what adb does.
    /// </summary>
    public async Task<string> DescribeAdbAsync(CancellationToken cancellation)
    {
        AdbCommandResult result = await adb.RunAsync(["version"], _timeout, cancellation).ConfigureAwait(false);
        return result.Status switch
        {
            AdbCommandStatus.NotFound => "Not found (install the Android SDK Platform-Tools)",
            AdbCommandStatus.CouldNotStart => "Found but could not be started",
            AdbCommandStatus.TimedOut => "Did not answer in time",
            AdbCommandStatus.Cancelled => "Not checked",
            _ when !result.Succeeded => $"Failed (exit code {result.ExitCode})",
            _ => ParseVersion(result.StandardOutput) ?? "Answered, version not recognised",
        };
    }

    /// <summary>"1.0.41 (Platform-Tools 35.0.2)" from <c>adb version</c>; null if neither line is there.</summary>
    internal static string? ParseVersion(string output)
    {
        string? bridge = null;
        string? tools = null;
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Android Debug Bridge version ", StringComparison.Ordinal))
            {
                bridge = line["Android Debug Bridge version ".Length..].Trim();
            }
            else if (line.StartsWith("Version ", StringComparison.Ordinal))
            {
                tools = line["Version ".Length..].Split('-')[0].Trim();
            }
        }

        return bridge is null ? null : tools is null ? bridge : $"{bridge} (Platform-Tools {tools})";
    }
}
