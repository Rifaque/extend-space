using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExtendSpace.Core.Wireless;

/// <summary>One resource record of an mDNS message; only the types DNS-SD browsing needs are decoded.</summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, string? Target = null, ushort Port = 0, IPAddress? Address = null, IReadOnlyList<string>? Text = null)
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
}

/// <summary>
/// The DNS message format as mDNS uses it (RFC 1035, RFC 6762), for queries and answers (D103). Pure:
/// bytes in, records out; malformed input throws <see cref="FormatException"/>.
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;

    /// <summary>A query for <paramref name="names"/> (each with its type), id 0 as mDNS expects.</summary>
    public static byte[] Query(IEnumerable<(string Name, ushort Type)> questions)
    {
        var buffer = new List<byte>(512);
        var list = questions.ToList();
        buffer.AddRange(new byte[] { 0, 0, 0, 0, (byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0, 0, 0 });
        foreach ((string name, ushort type) in list)
        {
            foreach (string label in name.TrimEnd('.').Split('.'))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                {
                    throw new ArgumentException($"Label '{label}' of '{name}' is not 1-63 bytes.", nameof(questions));
                }

                buffer.Add((byte)bytes.Length);
                buffer.AddRange(bytes);
            }

            buffer.Add(0);
            buffer.AddRange(new[] { (byte)(type >> 8), (byte)type, (byte)0, (byte)1 });
        }

        return [.. buffer];
    }

    /// <summary>Every answer, authority and additional record of a response; questions are skipped.</summary>
    public static IReadOnlyList<DnsRecord> Records(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
        {
            throw new FormatException("Shorter than a DNS header.");
        }

        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(message[6..]) + BinaryPrimitives.ReadUInt16BigEndian(message[8..]) + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
        int offset = HeaderLength;
        for (int i = 0; i < questions; i++)
        {
            ReadName(message, ref offset);
            offset += 4;
        }

        var result = new List<DnsRecord>(records);
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(message, ref offset);
            Need(message, offset, 10);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;
            Need(message, offset, length);
            int data = offset;
            ReadOnlySpan<byte> rdata = message.Slice(offset, length);
            offset += length;
            switch (type)
            {
                case DnsRecord.Ptr:
                    result.Add(new DnsRecord(name, type, ttl, Target: ReadName(message, ref data)));
                    break;
                case DnsRecord.Srv when length >= 7:
                    int target = data + 6;
                    result.Add(new DnsRecord(name, type, ttl, Target: ReadName(message, ref target), Port: BinaryPrimitives.ReadUInt16BigEndian(rdata[4..])));
                    break;
                case DnsRecord.A when length == 4:
                case DnsRecord.Aaaa when length == 16:
                    result.Add(new DnsRecord(name, type, ttl, Address: new IPAddress(rdata)));
                    break;
                case DnsRecord.Txt:
                    var strings = new List<string>();
                    for (int at = 0; at < rdata.Length;)
                    {
                        int size = rdata[at++];
                        if (at + size > rdata.Length)
                        {
                            throw new FormatException("TXT string runs past its record.");
                        }

                        if (size > 0)
                        {
                            strings.Add(Encoding.UTF8.GetString(rdata.Slice(at, size)));
                        }

                        at += size;
                    }

                    result.Add(new DnsRecord(name, type, ttl, Text: strings));
                    break;
            }
        }

        return result;
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        int at = offset;
        bool jumped = false;
        for (int hops = 0; ; hops++)
        {
            if (hops > 64)
            {
                throw new FormatException("A name points in a loop.");
            }

            Need(message, at, 1);
            int length = message[at];
            if (length == 0)
            {
                at++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                Need(message, at, 2);
                int pointer = BinaryPrimitives.ReadUInt16BigEndian(message[at..]) & 0x3FFF;
                if (!jumped)
                {
                    offset = at + 2;
                }

                jumped = true;
                at = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new FormatException("Unknown label type.");
            }

            Need(message, at + 1, length);
            labels.Add(Encoding.UTF8.GetString(message.Slice(at + 1, length)));
            at += 1 + length;
        }

        if (!jumped)
        {
            offset = at;
        }

        return string.Join('.', labels);
    }

    private static void Need(ReadOnlySpan<byte> message, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > message.Length)
        {
            throw new FormatException("A record runs past the end of the message.");
        }
    }
}

/// <summary>
/// DNS-SD browsing by plain mDNS queries (D103), for hosts without the Windows DNS-SD API. It shares
/// port 5353 with any daemon (address reuse) and hears multicast answers; if the port cannot be shared,
/// it falls back to one-shot queries from an ephemeral port (RFC 6762 section 6.7). No Avahi needed. Queries are repeated (1 s, then every 3 s
/// for 15 s, then every 30 s) and services expire with their TTL or a goodbye (TTL 0).
/// </summary>
public sealed class MulticastDnsBrowser : IServiceBrowser, IDisposable
{
    public const string ServiceType = "_extendspace._tcp";
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    private readonly string _service;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IPAddress Address, DateTime Expires)> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _running;
    private Socket? _socket;
    private int _received;

    public MulticastDnsBrowser(string serviceType = ServiceType, Action<string>? log = null)
    {
        _service = serviceType.TrimEnd('.') + ".local";
        _log = log;
    }

    public event Action<DiscoveredService>? Found;

    public event Action<string>? Lost;

    public void Start()
    {
        lock (_gate)
        {
            if (_running is not null)
            {
                return;
            }

            _socket = OpenShared5353() ?? OpenEphemeral();
            if (_socket is null)
            {
                return;
            }

            _running = new CancellationTokenSource();
            CancellationToken token = _running.Token;
            Socket socket = _socket;
            _ = Task.Run(() => ReceiveAsync(socket, token));
            _ = Task.Run(() => QueryAsync(socket, token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
            _socket?.Dispose();
            _socket = null;
            _entries.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The usual mDNS client socket: port 5353 shared with any daemon (address reuse), joined to the group
    /// on every IPv4 interface, so multicast answers arrive. Null when the port cannot be shared.
    /// </summary>
    private Socket? OpenShared5353()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Group.Port));
            int joined = 0;
            foreach (IPAddress local in LocalIPv4Addresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, local));
                    joined++;
                }
                catch (SocketException)
                {
                    // An interface without multicast; the others still work.
                }
            }

            if (joined == 0)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address));
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            _log?.Invoke($"mdns: listening on port 5353 ({joined} interfaces)");
            return socket;
        }
        catch (SocketException e)
        {
            _log?.Invoke($"mdns: port 5353 not shareable ({e.SocketErrorCode}); using one-shot queries");
            socket.Dispose();
            return null;
        }
    }

    /// <summary>One-shot queries from an ephemeral port (RFC 6762 section 6.7); answers come by unicast.</summary>
    private Socket? OpenEphemeral()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return socket;
        }
        catch (SocketException e)
        {
            _log?.Invoke($"mdns: cannot open a socket: {e.SocketErrorCode}");
            socket.Dispose();
            return null;
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.SupportsMulticast
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Applies one received message; public for tests. Returns the services that changed.</summary>
    public IReadOnlyList<DiscoveredService> Apply(IReadOnlyList<DnsRecord> records, DateTime now)
    {
        var changed = new List<DiscoveredService>();
        var lost = new List<string>();
        lock (_gate)
        {
            foreach (DnsRecord record in records.Where(r => r.Type is DnsRecord.A or DnsRecord.Aaaa && r.Address is not null))
            {
                _hosts[record.Name] = (record.Address!, now.AddSeconds(Math.Max(1, record.Ttl)));
            }

            foreach (DnsRecord record in records.Where(r => r.Type == DnsRecord.Ptr && string.Equals(r.Name, _service, StringComparison.OrdinalIgnoreCase) && r.Target is not null))
            {
                if (record.Ttl == 0)
                {
                    if (_entries.Remove(record.Target!))
                    {
                        lost.Add(record.Target!);
                    }

                    continue;
                }

                if (!_entries.TryGetValue(record.Target!, out Entry? entry))
                {
                    entry = new Entry(record.Target!);
                    _entries[record.Target!] = entry;
                }

                entry.Expires = now.AddSeconds(record.Ttl);
            }

            foreach (Entry entry in _entries.Values)
            {
                bool touched = false;
                foreach (DnsRecord record in records.Where(r => string.Equals(r.Name, entry.Instance, StringComparison.OrdinalIgnoreCase)))
                {
                    if (record.Type == DnsRecord.Srv)
                    {
                        touched |= entry.Host != record.Target || entry.Port != record.Port;
                        (entry.Host, entry.Port) = (record.Target, record.Port);
                    }
                    else if (record.Type == DnsRecord.Txt && record.Text is not null)
                    {
                        var text = record.Text.Select(t => t.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
                        touched |= !text.OrderBy(p => p.Key).SequenceEqual(entry.Text.OrderBy(p => p.Key));
                        entry.Text = text;
                    }
                }

                IPAddress? address = entry.Host is not null && _hosts.TryGetValue(entry.Host, out var host) && host.Expires > now ? host.Address : null;
                touched |= !Equals(address, entry.Address);
                entry.Address = address;
                if (touched && entry.Port != 0 && address is not null)
                {
                    changed.Add(entry.Describe());
                }
            }
        }

        foreach (string key in lost)
        {
            Lost?.Invoke(key);
        }

        foreach (DiscoveredService service in changed)
        {
            Found?.Invoke(service);
        }

        return changed;
    }

    /// <summary>Forgets services whose PTR record expired; public for tests.</summary>
    public void Expire(DateTime now)
    {
        List<string> gone;
        lock (_gate)
        {
            gone = [.. _entries.Values.Where(e => e.Expires <= now).Select(e => e.Instance)];
            foreach (string key in gone)
            {
                _entries.Remove(key);
            }
        }

        foreach (string key in gone)
        {
            Lost?.Invoke(key);
        }
    }

    /// <summary>The follow-up questions for services seen without an address or port yet.</summary>
    public IReadOnlyList<(string Name, ushort Type)> Missing()
    {
        lock (_gate)
        {
            var questions = new List<(string, ushort)>();
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Port == 0)
                {
                    questions.Add((entry.Instance, DnsRecord.Srv));
                    questions.Add((entry.Instance, DnsRecord.Txt));
                }
                else if (entry.Address is null && entry.Host is not null)
                {
                    questions.Add((entry.Host, DnsRecord.A));
                }
            }

            return questions;
        }
    }

    private async Task QueryAsync(Socket socket, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var questions = new List<(string, ushort)> { (_service, DnsRecord.Ptr) };
                questions.AddRange(Missing());
                await socket.SendToAsync(DnsMessage.Query(questions), SocketFlags.None, Group, token).ConfigureAwait(false);
                Expire(DateTime.UtcNow);
                TimeSpan age = DateTime.UtcNow - started;
                await Task.Delay(age < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(1) : age < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            _log?.Invoke($"mdns: query stopped: {e.Message}");
        }
    }

    private async Task ReceiveAsync(Socket socket, CancellationToken token)
    {
        byte[] buffer = new byte[9000];
        while (!token.IsCancellationRequested)
        {
            int length;
            try
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
                length = received.ReceivedBytes;
                if (++_received <= 3)
                {
                    _log?.Invoke($"mdns: {length} bytes from {received.RemoteEndPoint}");
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                Apply(DnsMessage.Records(buffer.AsSpan(0, length)), DateTime.UtcNow);
            }
            catch (FormatException e)
            {
                _log?.Invoke($"mdns: ignored a malformed message: {e.Message}");
            }
        }
    }

    private sealed class Entry(string instance)
    {
        public string Instance { get; } = instance;

        public DateTime Expires { get; set; }

        public string? Host { get; set; }

        public ushort Port { get; set; }

        public IPAddress? Address { get; set; }

        public Dictionary<string, string> Text { get; set; } = [];

        public DiscoveredService Describe()
        {
            string label = Instance.Split('.')[0];
            return new DiscoveredService(
                Instance,
                label,
                DiscoveredService.ParseIdPrefix(Text.GetValueOrDefault("id")),
                Address is null ? [] : [Address],
                Port,
                Text.GetValueOrDefault("p") == "1",
                int.TryParse(Text.GetValueOrDefault("v"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major) ? major : null);
        }
    }
}
