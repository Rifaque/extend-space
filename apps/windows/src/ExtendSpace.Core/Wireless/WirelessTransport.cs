using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Wireless;

/// <summary>Fault codes the wireless route reports.</summary>
public static class WirelessFaultCodes
{
    /// <summary>Terminal: no tablet was chosen for a wireless session.</summary>
    public const string NoDeviceChosen = "wireless_no_device";

    /// <summary>Terminal: the chosen tablet is not paired with this PC.</summary>
    public const string NotPaired = "not_paired";

    /// <summary>Retryable: the tablet is not on the network now (not advertising, no last address).</summary>
    public const string DeviceNotFound = "wireless_device_not_found";

    /// <summary>Terminal: the tablet's key is not the paired one (reinstalled, or not the same device).</summary>
    public const string AuthenticationFailed = "authentication_failed";

    /// <summary>Retryable: TLS could not be completed for a reason other than the key.</summary>
    public const string TlsFailed = "tls_failed";
}

/// <summary>
/// The Wi-Fi route (transport architecture sections 2 and 4): where the chosen paired tablet is now,
/// and mutual TLS 1.3 with its pinned key. Nothing to set up or release: the tablet listens.
/// </summary>
public sealed class WirelessTransport(
    Func<HostIdentity> identity,
    TrustedDeviceStore trust,
    IWirelessTargets targets,
    Func<string?> deviceIdSource,
    Action<string>? log = null) : IHostTransport
{
    private string? _deviceId;
    private IPEndPoint? _endPoint;

    public TransportKind Kind => TransportKind.Wireless;

    public Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return Task.FromResult(AttemptOutcome<TransportEndpoint>.Cancelled);
        }

        string? id = deviceIdSource();
        if (id is null)
        {
            return Failed(FaultTreatment.Terminal, WirelessFaultCodes.NoDeviceChosen, "No tablet was chosen for the wireless connection.");
        }

        if (trust.Find(id) is null)
        {
            return Failed(FaultTreatment.Terminal, WirelessFaultCodes.NotPaired, "This tablet is not paired with this PC.");
        }

        IPEndPoint? endPoint = targets.Resolve(id);
        if (endPoint is null)
        {
            return Failed(FaultTreatment.Retryable, WirelessFaultCodes.DeviceNotFound, "The tablet was not found on the network.");
        }

        _deviceId = id;
        _endPoint = endPoint;
        return Task.FromResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(endPoint)));
    }

    public async Task<AttemptOutcome<Stream>> SecureAsync(Socket connected, CancellationToken cancellation)
    {
        string id = _deviceId ?? throw new InvalidOperationException("PrepareAsync has not succeeded.");
        try
        {
            (System.Net.Security.SslStream tls, string _) = await WirelessTls.ConnectAsync(
                new NetworkStream(connected, ownsSocket: false), identity(), WirelessTls.SessionProtocol, id, cancellation).ConfigureAwait(false);
            if (_endPoint is { } endPoint)
            {
                trust.RecordAddress(id, endPoint.Address.ToString(), endPoint.Port);
            }

            return AttemptOutcome<Stream>.Succeeded(tls);
        }
        catch (PinMismatchException mismatch)
        {
            log?.Invoke($"wireless: {mismatch.Message}");
            return AttemptOutcome<Stream>.Failed(new SessionFault(
                FaultCategory.Protocol,
                WirelessFaultCodes.AuthenticationFailed,
                "The tablet's identity does not match the one paired with this PC. If ExtendSpace was reinstalled on it, forget it and pair again.",
                FaultTreatment.Terminal,
                UserActionable: true));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return AttemptOutcome<Stream>.Cancelled;
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException or ObjectDisposedException)
        {
            return AttemptOutcome<Stream>.Failed(new SessionFault(
                FaultCategory.Connection,
                WirelessFaultCodes.TlsFailed,
                $"The secure connection to the tablet could not be set up: {exception.Message}",
                FaultTreatment.Retryable));
        }
    }

    public Task ReleaseAsync(CancellationToken cancellation)
    {
        _deviceId = null;
        _endPoint = null;
        return Task.CompletedTask;
    }

    private static Task<AttemptOutcome<TransportEndpoint>> Failed(FaultTreatment treatment, string code, string message) =>
        Task.FromResult(AttemptOutcome<TransportEndpoint>.Failed(new SessionFault(
            code == WirelessFaultCodes.NotPaired ? FaultCategory.Protocol : FaultCategory.Transport, code, message, treatment, UserActionable: true)));
}
