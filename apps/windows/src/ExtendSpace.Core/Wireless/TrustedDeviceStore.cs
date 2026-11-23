using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExtendSpace.Core.Wireless;

/// <summary>A tablet this PC has paired with. Holds no secret: only the hash of its public key.</summary>
/// <param name="LastAddress">Where it was last reached, used when discovery does not find it.</param>
public sealed record TrustedDevice(string DeviceId, string Name, DateTimeOffset PairedAt, string? LastAddress = null, int? LastPort = null);

/// <summary>
/// The tablets this PC trusts (D79): a JSON file, rewritten whole on each change. Thread-safe.
/// A file that cannot be read is set aside and the list starts empty, so that a damaged file
/// fails closed (nothing trusted) rather than open.
/// </summary>
public sealed class TrustedDeviceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private List<TrustedDevice> _devices;

    public TrustedDeviceStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _devices = Load();
    }

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<TrustedDevice> List()
    {
        lock (_gate)
        {
            return [.. _devices];
        }
    }

    public TrustedDevice? Find(string? deviceId)
    {
        lock (_gate)
        {
            return deviceId is null ? null : _devices.FirstOrDefault(device => device.DeviceId == deviceId);
        }
    }

    /// <summary>Adds the device, or replaces the entry with the same ID.</summary>
    public void Add(TrustedDevice device)
    {
        if (!DeviceIds.IsValid(device.DeviceId))
        {
            throw new ArgumentException("Not a device ID.", nameof(device));
        }

        Mutate(list =>
        {
            list.RemoveAll(existing => existing.DeviceId == device.DeviceId);
            list.Add(device);
            return true;
        });
    }

    /// <summary>Forgets the device. False if it was not trusted.</summary>
    public bool Remove(string deviceId) => Mutate(list => list.RemoveAll(device => device.DeviceId == deviceId) > 0);

    /// <summary>Remembers where the device answered last. Ignored for a device that is not trusted.</summary>
    public void RecordAddress(string deviceId, string address, int port) => Mutate(list =>
    {
        int index = list.FindIndex(device => device.DeviceId == deviceId);
        if (index < 0 || (list[index].LastAddress == address && list[index].LastPort == port))
        {
            return false;
        }

        list[index] = list[index] with { LastAddress = address, LastPort = port };
        return true;
    });

    private bool Mutate(Func<List<TrustedDevice>, bool> change)
    {
        lock (_gate)
        {
            var next = new List<TrustedDevice>(_devices);
            if (!change(next))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new StoreFile(1, next), Json));
            _devices = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private List<TrustedDevice> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            StoreFile? file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(_path), Json);
            return file?.Devices?.Where(device => DeviceIds.IsValid(device.DeviceId) && device.Name is not null).ToList() ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            File.Move(_path, _path + ".unreadable", overwrite: true);
            _log?.Invoke($"the list of paired tablets could not be read ({exception.GetType().Name}); it starts empty");
            return [];
        }
    }

    private sealed record StoreFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("devices")] List<TrustedDevice>? Devices);
}
