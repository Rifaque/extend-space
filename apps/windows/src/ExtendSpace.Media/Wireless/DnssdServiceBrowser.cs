using System.Net;
using ExtendSpace.Core.Wireless;
using Windows.Devices.Enumeration;

namespace ExtendSpace.Media.Wireless;

/// <summary>
/// Browses <c>_extendspace._tcp.local</c> through Windows' own DNS-SD (D78): a device watcher on the
/// DNS-SD association-endpoint protocol, which reports records as they appear, change and expire.
/// No socket of ours is opened; port 5353 stays Windows'.
/// </summary>
public sealed class DnssdServiceBrowser : IServiceBrowser, IDisposable
{
    public const string ServiceType = "_extendspace._tcp";

    /// <summary>The DNS-SD protocol of Windows' association-endpoint enumeration.</summary>
    private const string DnssdProtocolId = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";

    private static readonly string[] Properties =
    [
        "System.Devices.Dnssd.InstanceName",
        "System.Devices.Dnssd.HostName",
        "System.Devices.Dnssd.PortNumber",
        "System.Devices.Dnssd.TextAttributes",
        "System.Devices.IpAddress",
    ];

    private readonly string _serviceType;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceInformation> _known = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;

    public DnssdServiceBrowser(string serviceType = ServiceType, Action<string>? log = null)
    {
        _serviceType = serviceType;
        _log = log;
    }

    public event Action<DiscoveredService>? Found;

    public event Action<string>? Lost;

    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            {
                return;
            }

            try
            {
                string query = $"System.Devices.AepService.ProtocolId:={DnssdProtocolId} AND System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"{_serviceType}\"";
                DeviceWatcher watcher = DeviceInformation.CreateWatcher(query, Properties, DeviceInformationKind.AssociationEndpointService);
                watcher.Added += OnAdded;
                watcher.Updated += OnUpdated;
                watcher.Removed += OnRemoved;
                watcher.Stopped += OnStopped;
                watcher.Start();
                _watcher = watcher;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                _log?.Invoke($"wireless discovery could not start: {exception.Message}");
            }
        }
    }

    public void Stop()
    {
        DeviceWatcher? watcher;
        List<string> gone;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            gone = [.. _known.Keys];
            _known.Clear();
        }

        if (watcher is not null)
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }

        foreach (string key in gone)
        {
            Lost?.Invoke(key);
        }
    }

    public void Dispose() => Stop();

    private void OnAdded(DeviceWatcher sender, DeviceInformation information)
    {
        lock (_gate)
        {
            _known[information.Id] = information;
        }

        Publish(information);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        DeviceInformation? information;
        lock (_gate)
        {
            if (_known.TryGetValue(update.Id, out information))
            {
                information.Update(update);
            }
        }

        if (information is not null)
        {
            Publish(information);
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            _known.Remove(update.Id);
        }

        Lost?.Invoke(update.Id);
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        // Windows stops a watcher on its own when the network stack resets; browsing restarts.
        bool wanted;
        lock (_gate)
        {
            wanted = ReferenceEquals(_watcher, sender);
            if (wanted)
            {
                _watcher = null;
            }
        }

        if (wanted)
        {
            _log?.Invoke("wireless discovery stopped by Windows; restarting");
            Start();
        }
    }

    private void Publish(DeviceInformation information)
    {
        if (Read(information) is { } service)
        {
            Found?.Invoke(service);
        }
    }

    internal static DiscoveredService? Read(DeviceInformation information)
    {
        IReadOnlyDictionary<string, object> properties = information.Properties;
        string? instance = properties.TryGetValue("System.Devices.Dnssd.InstanceName", out object? name) ? name as string : null;
        int port = properties.TryGetValue("System.Devices.Dnssd.PortNumber", out object? portValue) && portValue is ushort p ? p : 0;
        string[] text = properties.TryGetValue("System.Devices.Dnssd.TextAttributes", out object? txt) && txt is string[] attributes ? attributes : [];
        string[] addresses = properties.TryGetValue("System.Devices.IpAddress", out object? ip) && ip is string[] list ? list : [];
        return ToService(information.Id, instance ?? information.Name, port, text, addresses);
    }

    /// <summary>The service a DNS-SD record describes; separated from WinRT so that it can be tested.</summary>
    internal static DiscoveredService ToService(string key, string instanceName, int port, IEnumerable<string> textAttributes, IEnumerable<string> addresses)
    {
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string attribute in textAttributes)
        {
            int equals = attribute.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                text.TryAdd(attribute[..equals], attribute[(equals + 1)..]);
            }
        }

        List<IPAddress> parsed = [.. addresses.Select(address => IPAddress.TryParse(address, out IPAddress? value) ? value : null).OfType<IPAddress>()];
        return new DiscoveredService(
            key,
            instanceName,
            DiscoveredService.ParseIdPrefix(text.GetValueOrDefault("id")),
            parsed,
            port,
            text.GetValueOrDefault("p") == "1",
            int.TryParse(text.GetValueOrDefault("v"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major) ? major : null);
    }
}
