using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Wireless;

/// <summary>
/// Everything wireless on the PC, put together once (D74–D79): this PC's identity, the trusted
/// tablets, what discovery has found, pairing and forgetting. The session reaches it only through the
/// transport it creates; the Connect screen uses the rest.
/// </summary>
public sealed class WirelessHost
{
    private readonly PairingOptions _pairing;
    private readonly Action<string>? _log;

    public WirelessHost(HostIdentityStore identity, TrustedDeviceStore trust, IServiceBrowser? browser, PairingOptions? pairing = null, Action<string>? log = null)
    {
        Identity = identity;
        Trust = trust;
        Browser = browser;
        Directory = new WirelessDirectory(trust, browser);
        _pairing = pairing ?? new PairingOptions();
        _log = log;
    }

    /// <summary>The usual place: <c>%LOCALAPPDATA%\ExtendSpace</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExtendSpace");

    public HostIdentityStore Identity { get; }

    public TrustedDeviceStore Trust { get; }

    public WirelessDirectory Directory { get; }

    public IServiceBrowser? Browser { get; }

    public IHostTransport CreateTransport(HostDeviceSelection selection) =>
        new WirelessTransport(Identity.LoadOrCreate, Trust, Directory, () => selection.WirelessDeviceId, _log);

    /// <summary>Pairs with a tablet that is in pairing mode. The user is asked through <paramref name="confirm"/>.</summary>
    public Task<PairingResult> PairAsync(WirelessDeviceEntry device, PairingConfirmation confirm, CancellationToken cancellation)
    {
        if (device.EndPoint is not { } endPoint)
        {
            return Task.FromResult(new PairingResult(PairingStatus.Failed, $"{device.Name} is not on the network now."));
        }

        return Task.Run(async () =>
        {
            PairingResult result = await new WirelessPairing(Identity.LoadOrCreate(), Trust, _pairing, _log).PairAsync(endPoint, device.Name, confirm, cancellation).ConfigureAwait(false);
            if (result is { Status: PairingStatus.Paired, Device: { } paired } && device.IdPrefix is null)
            {
                // A tablet added by address: now its key is known, it is matched to its pairing.
                Directory.Identify(device.Key, paired.DeviceId[..DiscoveredService.IdPrefixLength]);
            }

            return result;
        }, cancellation);
    }

    public bool Forget(string deviceId)
    {
        bool removed = Trust.Remove(deviceId);
        if (removed)
        {
            _log?.Invoke($"forgot tablet {DeviceIds.Short(deviceId)}");
        }

        return removed;
    }
}
