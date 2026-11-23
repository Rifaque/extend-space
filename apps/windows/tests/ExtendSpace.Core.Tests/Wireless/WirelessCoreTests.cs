using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.Tests.Wireless;

/// <summary>A reversible stand-in for DPAPI: the tests check what is stored, not DPAPI itself.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] secret) => [0x5A, .. secret.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] protectedData) =>
        protectedData is [0x5A, .. var rest] ? [.. rest.Select(b => (byte)(b ^ 0x5A))] : throw new CryptographicException("not ours");
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("extendspace-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class PairingCodeTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (JsonElement element in Load().GetProperty("cases").EnumerateArray())
        {
            data.Add(element.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_commitment_and_code_match_the_shared_vectors(string name)
    {
        JsonElement vector = Load().GetProperty("cases").EnumerateArray().Single(element => element.GetProperty("name").GetString() == name);
        byte[] Bytes(string field) => Convert.FromHexString(vector.GetProperty(field).GetString()!);

        Assert.Equal(vector.GetProperty("commitment").GetString(), Convert.ToHexStringLower(PairingCode.Commit(Bytes("host_nonce"))));
        Assert.Equal(vector.GetProperty("code").GetString(), PairingCode.Compute(Bytes("host_nonce"), Bytes("client_nonce"), Bytes("host_id"), Bytes("client_id")));
    }

    [Fact]
    public void The_code_depends_on_every_input_and_keeps_leading_zeros()
    {
        byte[] a = new byte[32], b = new byte[32];
        b[31] = 1;
        string baseline = PairingCode.Compute(a, a, a, a);
        Assert.Equal(6, baseline.Length);
        Assert.NotEqual(baseline, PairingCode.Compute(b, a, a, a));
        Assert.NotEqual(baseline, PairingCode.Compute(a, b, a, a));
        Assert.NotEqual(baseline, PairingCode.Compute(a, a, b, a));
        Assert.NotEqual(baseline, PairingCode.Compute(a, a, a, b));
        Assert.Equal("014 363", PairingCode.Format("014363"));
    }

    [Fact]
    public void Inputs_of_the_wrong_length_are_refused()
    {
        Assert.Throws<ArgumentException>(() => PairingCode.Commit(new byte[31]));
        Assert.Throws<ArgumentException>(() => PairingCode.Compute(new byte[32], new byte[32], new byte[32], new byte[33]));
    }

    private static JsonElement Load() => JsonDocument.Parse(File.ReadAllText(SharedTestData.Find("tests/pairing-vectors/sas-v1.json"))).RootElement;
}

public sealed class WirelessCodecTests
{
    [Fact]
    public void Pairing_messages_round_trip()
    {
        byte[] value = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(value, Assert.IsType<PairCommitMessage>(RoundTrip(new PairCommitMessage(value))).Commitment);
        Assert.Equal(value, Assert.IsType<PairNonceMessage>(RoundTrip(new PairNonceMessage(value))).Nonce);
        Assert.Equal(value, Assert.IsType<PairRevealMessage>(RoundTrip(new PairRevealMessage(value))).Nonce);
        Assert.True(Assert.IsType<PairResultMessage>(RoundTrip(new PairResultMessage(true))).Accepted);
        Assert.False(Assert.IsType<PairResultMessage>(RoundTrip(new PairResultMessage(false))).Accepted);
    }

    [Theory]
    [InlineData("{\"nonce\":\"AAAA\"}")]
    [InlineData("{\"nonce\":\"not base64!\"}")]
    [InlineData("{\"nonce\":5}")]
    [InlineData("{}")]
    public void A_pairing_value_that_is_not_32_bytes_of_base64_is_malformed(string json)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(() => ControlCodec.Decode((byte)ControlType.PairNonce, System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Equal(WireErrorCode.MalformedMessage, error.Code);
    }

    [Fact]
    public void Hello_carries_an_optional_purpose()
    {
        Assert.Equal("pair", Assert.IsType<HelloMessage>(RoundTrip(new HelloMessage(0, 0, "host", "PC", "1", "pair"))).Purpose);
        Assert.Null(Assert.IsType<HelloMessage>(RoundTrip(new HelloMessage(0, 0, "host", "PC", "1"))).Purpose);
    }

    [Fact]
    public void Stream_config_offers_input_only_when_asked_and_old_messages_read_as_no_input()
    {
        var config = new StreamConfigMessage("h264", "high", 1920, 1080, 60, 12_000_000, 1 << 20, null, null);
        Assert.False(Assert.IsType<StreamConfigMessage>(RoundTrip(config)).InputTouch);
        Assert.DoesNotContain("input", System.Text.Encoding.UTF8.GetString(ControlCodec.Encode(config).AsSpan(WireFormat.HeaderLength)));
        Assert.True(Assert.IsType<StreamConfigMessage>(RoundTrip(config with { InputTouch = true })).InputTouch);
    }

    private static object? RoundTrip(object message)
    {
        byte[] frame = ControlCodec.Encode(message);
        return ControlCodec.Decode(frame[1], frame.AsSpan(WireFormat.HeaderLength));
    }
}

public sealed class WirelessStoreTests
{
    private static readonly string IdA = new('a', 64);
    private static readonly string IdB = new('b', 64);

    [Fact]
    public void Trusted_devices_persist_replace_and_forget()
    {
        using var folder = new TempFolder();
        var store = new TrustedDeviceStore(folder.File("trusted.json"));
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Add(new TrustedDevice(IdA, "Pad", DateTimeOffset.UnixEpoch));
        store.Add(new TrustedDevice(IdB, "Phone", DateTimeOffset.UnixEpoch));
        store.Add(new TrustedDevice(IdA, "Pad renamed", DateTimeOffset.UnixEpoch));
        store.RecordAddress(IdA, "192.168.1.20", 27184);
        store.RecordAddress(IdA, "192.168.1.20", 27184);

        var reopened = new TrustedDeviceStore(folder.File("trusted.json"));
        Assert.Equal(2, reopened.List().Count);
        Assert.Equal(("Pad renamed", "192.168.1.20", (int?)27184), (reopened.Find(IdA)!.Name, reopened.Find(IdA)!.LastAddress, reopened.Find(IdA)!.LastPort));
        Assert.Equal(4, changes);

        Assert.True(reopened.Remove(IdA));
        Assert.False(reopened.Remove(IdA));
        Assert.Null(new TrustedDeviceStore(folder.File("trusted.json")).Find(IdA));
        Assert.DoesNotContain(Directory.GetFiles(folder.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damaged_trust_file_fails_closed_and_is_set_aside()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("trusted.json"), "{ not json");
        var lines = new List<string>();

        var store = new TrustedDeviceStore(folder.File("trusted.json"), lines.Add);

        Assert.Empty(store.List());
        Assert.True(File.Exists(folder.File("trusted.json.unreadable")));
        Assert.Single(lines);
    }

    [Fact]
    public void Entries_that_are_not_device_ids_are_refused_and_ignored_on_load()
    {
        using var folder = new TempFolder();
        var store = new TrustedDeviceStore(folder.File("trusted.json"));
        Assert.Throws<ArgumentException>(() => store.Add(new TrustedDevice("ABC", "x", DateTimeOffset.UnixEpoch)));

        File.WriteAllText(folder.File("other.json"), $"{{\"version\":1,\"devices\":[{{\"device_id\":\"{IdA.ToUpperInvariant()}\",\"name\":\"x\",\"paired_at\":\"2026-01-01T00:00:00Z\"}}]}}");
        Assert.Empty(new TrustedDeviceStore(folder.File("other.json")).List());
    }

    [Fact]
    public void The_identity_is_created_once_protected_and_read_back_with_the_same_id()
    {
        using var folder = new TempFolder();
        var protector = new XorProtector();
        HostIdentity first = new HostIdentityStore(folder.File("identity.bin"), protector).LoadOrCreate();
        HostIdentity second = new HostIdentityStore(folder.File("identity.bin"), protector).LoadOrCreate();

        Assert.True(DeviceIds.IsValid(first.DeviceId));
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.True(second.Certificate.HasPrivateKey);
        Assert.Equal(0x5A, File.ReadAllBytes(folder.File("identity.bin"))[0]);
    }

    [Fact]
    public void An_unreadable_identity_is_replaced_and_kept_aside()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.File("identity.bin"), [1, 2, 3]);
        var lines = new List<string>();

        HostIdentity identity = new HostIdentityStore(folder.File("identity.bin"), new XorProtector(), lines.Add).LoadOrCreate();

        Assert.True(DeviceIds.IsValid(identity.DeviceId));
        Assert.True(File.Exists(folder.File("identity.bin.unreadable")));
        Assert.Contains(lines, line => line.Contains("paired again", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_ids_are_the_hash_of_the_public_key()
    {
        HostIdentity identity = HostIdentity.Create();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(identity.Certificate.PublicKey.ExportSubjectPublicKeyInfo())), identity.DeviceId);
        Assert.Equal($"{identity.DeviceId[..4]}-{identity.DeviceId[4..8]}", DeviceIds.Short(identity.DeviceId));
        Assert.False(DeviceIds.IsValid(identity.DeviceId.ToUpperInvariant()));
    }
}

public sealed class WirelessDirectoryTests : IDisposable
{
    private static readonly string Id = "0123456789abcdef" + new string('c', 48);
    private readonly TempFolder _folder = new();
    private readonly TrustedDeviceStore _trust;
    private readonly WirelessDirectory _directory;

    public WirelessDirectoryTests()
    {
        _trust = new TrustedDeviceStore(_folder.File("trusted.json"));
        _directory = new WirelessDirectory(_trust);
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_found_tablet_is_matched_to_its_pairing_by_prefix_and_listed_by_its_paired_name()
    {
        _trust.Add(new TrustedDevice(Id, "My Pad", DateTimeOffset.UnixEpoch));
        _directory.Upsert(Service("k1", "Xiaomi Pad 7", "0123456789abcdef", "192.168.1.9"));

        WirelessDeviceEntry entry = Assert.Single(_directory.Devices);
        Assert.Equal(("My Pad", Id, true, true), (entry.Name, entry.DeviceId, entry.IsPaired, entry.IsAvailable));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.9"), 27184), _directory.Resolve(Id));
    }

    [Fact]
    public void An_unpaired_tablet_is_listed_without_a_full_id_and_cannot_be_resolved()
    {
        _directory.Upsert(Service("k1", "Stranger", "ffffffffffffffff", "192.168.1.9", pairing: true));

        WirelessDeviceEntry entry = Assert.Single(_directory.Devices);
        Assert.Equal((false, (string?)null, true), (entry.IsPaired, entry.DeviceId, entry.PairingOpen));
    }

    [Fact]
    public void A_paired_tablet_that_goes_away_stays_listed_at_its_last_address()
    {
        _trust.Add(new TrustedDevice(Id, "My Pad", DateTimeOffset.UnixEpoch, "10.0.0.5", 27184));
        _directory.Upsert(Service("k1", "Pad", "0123456789abcdef", "192.168.1.9"));
        int changes = 0;
        _directory.Changed += (_, _) => changes++;

        _directory.Remove("k1");
        _directory.Remove("k1");

        // ES-126: where multicast is dropped the last address is the only way to reach it.
        WirelessDeviceEntry entry = Assert.Single(_directory.Devices);
        Assert.True(entry.IsAvailable);
        Assert.True(entry.IsLastKnown);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 27184), _directory.Resolve(Id));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_paired_tablet_never_reached_and_not_found_is_unavailable()
    {
        _trust.Add(new TrustedDevice(Id, "My Pad", DateTimeOffset.UnixEpoch));
        WirelessDeviceEntry entry = Assert.Single(_directory.Devices);
        Assert.False(entry.IsAvailable);
        Assert.Null(_directory.Resolve(Id));
    }

    [Fact]
    public void A_tablet_added_by_address_is_unpaired_until_identified_then_matches_its_pairing()
    {
        string key = _directory.AddManual(new IPEndPoint(IPAddress.Parse("192.168.1.193"), 27184));
        WirelessDeviceEntry manual = Assert.Single(_directory.Devices);
        Assert.Equal((key, false, true, true), (manual.Key, manual.IsPaired, manual.IsManual, manual.IsAvailable));

        _trust.Add(new TrustedDevice(Id, "My Pad", DateTimeOffset.UnixEpoch));
        _directory.Identify(key, Id[..16]);

        WirelessDeviceEntry paired = Assert.Single(_directory.Devices);
        Assert.Equal(("My Pad", Id, false), (paired.Name, paired.DeviceId, paired.IsLastKnown));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.193"), 27184), _directory.Resolve(Id));
    }

    [Theory]
    [InlineData("192.168.1.193", "192.168.1.193:27184")]
    [InlineData(" 192.168.1.193:30000 ", "192.168.1.193:30000")]
    [InlineData("[2001:db8::5]:27184", "[2001:db8::5]:27184")]
    [InlineData("tablet.local", null)]
    [InlineData("", null)]
    [InlineData("192.168.1.193:0", null)]
    public void Typed_addresses_are_read_with_the_default_port(string text, string? expected) =>
        Assert.Equal(expected, WirelessDirectory.ParseAddress(text)?.ToString());

    [Fact]
    public void The_same_tablet_seen_twice_is_listed_once_and_an_unchanged_record_raises_nothing()
    {
        _trust.Add(new TrustedDevice(Id, "My Pad", DateTimeOffset.UnixEpoch));
        _directory.Upsert(Service("k1", "Pad", "0123456789abcdef", "192.168.1.9"));
        int changes = 0;
        _directory.Changed += (_, _) => changes++;
        _directory.Upsert(Service("k1", "Pad", "0123456789abcdef", "192.168.1.9"));
        _directory.Upsert(Service("k2", "Pad (2)", "0123456789abcdef", "192.168.1.10"));

        Assert.Single(_directory.Devices);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void IPv4_is_preferred_link_local_IPv6_is_never_used_and_a_bad_port_gives_no_endpoint()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.9"), WirelessDirectory.Choose(Service("k", "p", null, "fe80::1", "2001:db8::5", "192.168.1.9"))!.Address);
        Assert.Equal(IPAddress.Parse("2001:db8::5"), WirelessDirectory.Choose(Service("k", "p", null, "fe80::1", "2001:db8::5"))!.Address);
        Assert.Null(WirelessDirectory.Choose(Service("k", "p", null, "fe80::1")));
        Assert.Null(WirelessDirectory.Choose(Service("k", "p", null, "192.168.1.9") with { Port = 0 }));
    }

    [Fact]
    public void A_tablet_speaking_another_protocol_major_is_marked_incompatible()
    {
        _directory.Upsert(Service("k1", "Future", "ffffffffffffffff", "192.168.1.9") with { ProtocolMajor = 1 });
        Assert.False(Assert.Single(_directory.Devices).IsCompatible);
    }

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("0123456789ABCDEF", null)]
    [InlineData("0123", null)]
    [InlineData(null, null)]
    public void Only_a_well_formed_id_prefix_is_used(string? txt, string? expected) =>
        Assert.Equal(expected, DiscoveredService.ParseIdPrefix(txt));

    internal static DiscoveredService Service(string key, string name, string? prefix, params string[] addresses) =>
        Service(key, name, prefix, addresses[0], pairing: false) with { Addresses = [.. addresses.Select(IPAddress.Parse)] };

    internal static DiscoveredService Service(string key, string name, string? prefix, string address, bool pairing) =>
        new(key, name, prefix, [IPAddress.Parse(address)], 27184, pairing, 0);
}

public sealed class RoutedTransportTests
{
    private sealed class Recording(TransportKind kind) : IHostTransport
    {
        public int Prepares { get; private set; }

        public int Releases { get; private set; }

        public TransportKind Kind => kind;

        public Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation)
        {
            Prepares++;
            return Task.FromResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(new IPEndPoint(IPAddress.Loopback, 1))));
        }

        public Task ReleaseAsync(CancellationToken cancellation)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_route_is_chosen_at_the_first_attempt_and_kept_until_release()
    {
        var selection = new HostDeviceSelection { Transport = TransportKind.Wireless };
        var usb = new Recording(TransportKind.Usb);
        var wireless = new Recording(TransportKind.Wireless);
        var routed = new RoutedTransport(selection, usb, wireless);

        await routed.PrepareAsync(TestContext.Current.CancellationToken);
        selection.Transport = TransportKind.Usb;
        await routed.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal((0, 2, TransportKind.Wireless), (usb.Prepares, wireless.Prepares, routed.Kind));

        await routed.ReleaseAsync(TestContext.Current.CancellationToken);
        await routed.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, 1, 0), (usb.Prepares, wireless.Releases, usb.Releases));
    }

    [Fact]
    public async Task USB_passes_the_socket_through_unchanged()
    {
        var routed = new RoutedTransport(new HostDeviceSelection(), new Recording(TransportKind.Usb), null);
        await routed.PrepareAsync(TestContext.Current.CancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
            using Socket accepted = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);

            AttemptOutcome<Stream> secured = await routed.SecureAsync(socket, TestContext.Current.CancellationToken);

            Assert.IsType<NetworkStream>(secured.Value);
            secured.Value!.Dispose();
            Assert.True(socket.Connected, "the stream must not own the socket");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_wireless_session_without_a_chosen_or_paired_tablet_fails_terminally()
    {
        using var folder = new TempFolder();
        var trust = new TrustedDeviceStore(folder.File("t.json"));
        var directory = new WirelessDirectory(trust);
        string? chosen = null;
        var transport = new WirelessTransport(() => throw new InvalidOperationException("not needed"), trust, directory, () => chosen);

        AttemptOutcome<TransportEndpoint> none = await transport.PrepareAsync(TestContext.Current.CancellationToken);
        chosen = new string('d', 64);
        AttemptOutcome<TransportEndpoint> unpaired = await transport.PrepareAsync(TestContext.Current.CancellationToken);
        trust.Add(new TrustedDevice(chosen, "Pad", DateTimeOffset.UnixEpoch));
        AttemptOutcome<TransportEndpoint> notFound = await transport.PrepareAsync(TestContext.Current.CancellationToken);

        Assert.Equal((WirelessFaultCodes.NoDeviceChosen, false), (none.Fault!.Code, none.Fault.IsRetryable));
        Assert.Equal((WirelessFaultCodes.NotPaired, false), (unpaired.Fault!.Code, unpaired.Fault.IsRetryable));
        Assert.Equal((WirelessFaultCodes.DeviceNotFound, true), (notFound.Fault!.Code, notFound.Fault.IsRetryable));
        Assert.Equal(SessionErrorCode.AuthenticationFailed, SessionErrors.From(unpaired.Fault, hasStreamed: false).Code);
    }
}
