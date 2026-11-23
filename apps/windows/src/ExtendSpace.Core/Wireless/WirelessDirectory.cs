using System.Net;
using System.Net.Sockets;

namespace ExtendSpace.Core.Wireless;

/// <summary>One DNS-SD answer for <c>_extendspace._tcp</c> (D78), as the browser reported it.</summary>
/// <param name="Key">The browser's own identity for the record; stable while the record lives.</param>
/// <param name="IdPrefix">TXT <c>id</c>: the first 16 hex digits of the tablet's device ID; null if absent or malformed.</param>
/// <param name="PairingOpen">TXT <c>p=1</c>: the tablet's user has its pairing window open.</param>
public sealed record DiscoveredService(
    string Key,
    string InstanceName,
    string? IdPrefix,
    IReadOnlyList<IPAddress> Addresses,
    int Port,
    bool PairingOpen,
    int? ProtocolMajor)
{
    public const int IdPrefixLength = 16;

    /// <summary>The TXT <c>id</c> value if it is the right shape; null otherwise.</summary>
    public static string? ParseIdPrefix(string? value) =>
        value is { Length: IdPrefixLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? value : null;
}

/// <summary>Finds ExtendSpace tablets on the network. Implemented over the OS's DNS-SD (ExtendSpace.Media).</summary>
public interface IServiceBrowser
{
    /// <summary>Raised, on any thread, when a service appears or its details change.</summary>
    event Action<DiscoveredService>? Found;

    /// <summary>Raised, on any thread, with the <see cref="DiscoveredService.Key"/> of a service that has gone.</summary>
    event Action<string>? Lost;

    /// <summary>Starts browsing; harmless when already started. Never throws for what the network does.</summary>
    void Start();

    void Stop();
}

/// <summary>A tablet as the Connect screen lists it: what was found on the network, joined with what this PC trusts.</summary>
/// <param name="DeviceId">The full ID for a paired tablet; null for one not paired (only its prefix is known).</param>
/// <param name="EndPoint">Where it can be reached now; null if it is paired but neither found nor ever reached.</param>
/// <param name="IsLastKnown">The end point is the address it last answered on, not a current discovery result.</param>
/// <param name="IsManual">Added by address on the Connect screen, for networks that drop multicast.</param>
public sealed record WirelessDeviceEntry(
    string Key,
    string Name,
    string? DeviceId,
    string? IdPrefix,
    bool IsPaired,
    IPEndPoint? EndPoint,
    bool PairingOpen,
    bool IsCompatible,
    bool IsLastKnown = false,
    bool IsManual = false)
{
    public bool IsAvailable => EndPoint is not null;
}

/// <summary>Where a paired tablet can be reached now. What the wireless transport asks before each attempt.</summary>
public interface IWirelessTargets
{
    IPEndPoint? Resolve(string deviceId);
}

/// <summary>
/// Keeps what the browser has found and joins it with the trust list (D78). The browser's results
/// are matched to paired tablets by the TXT ID prefix; the full ID is checked by TLS anyway, so a
/// wrong or forged prefix can at worst send a connection to a device that will fail the pin. A paired
/// tablet that is not found is still listed, as unavailable, with its last address as a fallback for
/// networks that drop multicast. Thread-safe.
/// </summary>
public sealed class WirelessDirectory : IWirelessTargets
{
    private readonly TrustedDeviceStore _trust;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DiscoveredService> _found = new(StringComparer.Ordinal);

    public WirelessDirectory(TrustedDeviceStore trust, IServiceBrowser? browser = null)
    {
        _trust = trust;
        _trust.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (browser is not null)
        {
            browser.Found += Upsert;
            browser.Lost += Remove;
        }
    }

    /// <summary>Raised, on any thread, when <see cref="Devices"/> may have changed.</summary>
    public event EventHandler? Changed;

    public void Upsert(DiscoveredService service)
    {
        lock (_gate)
        {
            if (_found.TryGetValue(service.Key, out DiscoveredService? known) && Same(known, service))
            {
                return;
            }

            _found[service.Key] = service;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _found.Remove(key);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The default port of a tablet's Wi-Fi endpoint (D86).</summary>
    public const int DefaultPort = 27184;

    /// <summary>
    /// A tablet the user typed in, for networks where discovery does not work (ES-126). It is listed as
    /// not paired until pairing tells this PC its key. Returns its key in <see cref="Devices"/>.
    /// </summary>
    public string AddManual(IPEndPoint endPoint)
    {
        string key = $"manual:{endPoint}";
        Upsert(new DiscoveredService(key, $"Tablet at {endPoint.Address}", null, [endPoint.Address], endPoint.Port, PairingOpen: true, ProtocolMajor: null));
        return key;
    }

    /// <summary>Parses what a user typed: an IP address, with an optional port. Null if it is not one.</summary>
    public static IPEndPoint? ParseAddress(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (IPEndPoint.TryParse(text, out IPEndPoint? endPoint) && endPoint.Port != 0)
        {
            return endPoint;
        }

        return IPAddress.TryParse(text, out IPAddress? address) ? new IPEndPoint(address, DefaultPort) : null;
    }

    /// <summary>Records the key prefix of a manually added tablet once pairing has learnt it, so that it matches its pairing.</summary>
    public void Identify(string key, string idPrefix)
    {
        bool changed;
        lock (_gate)
        {
            changed = _found.TryGetValue(key, out DiscoveredService? service) && service.IdPrefix != idPrefix;
            if (changed)
            {
                _found[key] = _found[key] with { IdPrefix = idPrefix };
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<WirelessDeviceEntry> Devices
    {
        get
        {
            List<DiscoveredService> found;
            lock (_gate)
            {
                found = [.. _found.Values];
            }

            IReadOnlyList<TrustedDevice> trusted = _trust.List();
            var entries = new List<WirelessDeviceEntry>();
            var listedPaired = new HashSet<string>(StringComparer.Ordinal);

            // Newest answer first per device, so that a stale record of a renamed or moved tablet loses.
            foreach (DiscoveredService service in found.OrderBy(service => service.InstanceName, StringComparer.CurrentCultureIgnoreCase))
            {
                TrustedDevice? paired = service.IdPrefix is null ? null : trusted.FirstOrDefault(device => device.DeviceId.StartsWith(service.IdPrefix, StringComparison.Ordinal));
                if (paired is not null && !listedPaired.Add(paired.DeviceId))
                {
                    continue;
                }

                entries.Add(new WirelessDeviceEntry(
                    service.Key,
                    paired?.Name ?? service.InstanceName,
                    paired?.DeviceId,
                    service.IdPrefix,
                    paired is not null,
                    Choose(service),
                    service.PairingOpen,
                    service.ProtocolMajor is null or Protocol.WireFormat.MajorVersion,
                    IsManual: service.Key.StartsWith("manual:", StringComparison.Ordinal)));
            }

            // A paired tablet that discovery does not see is still offered at the address it last
            // answered on: on networks that drop multicast that is the only way to reach it (ES-126).
            foreach (TrustedDevice device in trusted.Where(device => !listedPaired.Contains(device.DeviceId)))
            {
                IPEndPoint? last = device is { LastAddress: { } address, LastPort: { } port } && IPAddress.TryParse(address, out IPAddress? parsed) && port is > 0 and <= 65535
                    ? new IPEndPoint(parsed, port)
                    : null;
                entries.Add(new WirelessDeviceEntry("trusted:" + device.DeviceId, device.Name, device.DeviceId, device.DeviceId[..DiscoveredService.IdPrefixLength], true, last, false, true, IsLastKnown: last is not null));
            }

            return entries;
        }
    }

    public IPEndPoint? Resolve(string deviceId)
    {
        WirelessDeviceEntry? entry = Devices.FirstOrDefault(device => device.DeviceId == deviceId && device.EndPoint is not null);
        if (entry?.EndPoint is { } found)
        {
            return found;
        }

        // Not seen by discovery: the last address it answered on, if any.
        TrustedDevice? trusted = _trust.Find(deviceId);
        return trusted is { LastAddress: { } address, LastPort: { } port } && IPAddress.TryParse(address, out IPAddress? parsed)
            ? new IPEndPoint(parsed, port)
            : null;
    }

    /// <summary>IPv4 first, then a global IPv6 address, then loopback; link-local IPv6 needs a scope and is not used.</summary>
    internal static IPEndPoint? Choose(DiscoveredService service)
    {
        if (service.Port is <= 0 or > 65535)
        {
            return null;
        }

        // Loopback only as a last resort: a real tablet never advertises it, a test on one machine does.
        IPAddress? address = service.Addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            ?? service.Addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !IPAddress.IsLoopback(address))
            ?? service.Addresses.FirstOrDefault(IPAddress.IsLoopback);
        return address is null ? null : new IPEndPoint(address, service.Port);
    }

    private static bool Same(DiscoveredService a, DiscoveredService b) =>
        a.InstanceName == b.InstanceName && a.IdPrefix == b.IdPrefix && a.Port == b.Port && a.PairingOpen == b.PairingOpen
        && a.ProtocolMajor == b.ProtocolMajor && a.Addresses.SequenceEqual(b.Addresses);
}
