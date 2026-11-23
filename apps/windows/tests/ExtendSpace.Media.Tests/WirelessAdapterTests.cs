using System.Net;
using System.Security.Cryptography;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Media.Wireless;
using Windows.Networking;
using Windows.Networking.ServiceDiscovery.Dnssd;
using Windows.Networking.Sockets;

namespace ExtendSpace.Media.Tests;

public sealed class WirelessAdapterTests
{
    [Fact]
    public void DPAPI_round_trips_for_this_user_and_rejects_altered_data()
    {
        var protector = new DpapiSecretProtector();
        byte[] secret = RandomNumberGenerator.GetBytes(200);

        byte[] protectedData = protector.Protect(secret);
        Assert.NotEqual(secret, protectedData);
        Assert.Equal(secret, protector.Unprotect(protectedData));

        protectedData[^5] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedData));
    }

    [Fact]
    public void The_identity_store_works_with_DPAPI()
    {
        string folder = Directory.CreateTempSubdirectory("extendspace-dpapi-").FullName;
        try
        {
            string path = Path.Combine(folder, "identity.bin");
            HostIdentity created = new HostIdentityStore(path, new DpapiSecretProtector()).LoadOrCreate();
            HostIdentity read = new HostIdentityStore(path, new DpapiSecretProtector()).LoadOrCreate();
            Assert.Equal(created.DeviceId, read.DeviceId);
            Assert.True(read.Certificate.HasPrivateKey);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_DNS_SD_record_is_read_into_a_service()
    {
        DiscoveredService service = DnssdServiceBrowser.ToService(
            "key", "Xiaomi Pad 7", 27184, ["id=0123456789abcdef", "v=0", "p=1", "junk", "=x"], ["fe80::1%12", "192.168.1.9", "not an address"]);

        Assert.Equal(("0123456789abcdef", 27184, true, (int?)0), (service.IdPrefix, service.Port, service.PairingOpen, service.ProtocolMajor));
        Assert.Contains(IPAddress.Parse("192.168.1.9"), service.Addresses);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), WirelessDirectory.Choose(service)!.Address);
    }

    [Fact]
    public void A_record_without_or_with_a_bad_id_and_version_reads_as_unknown()
    {
        DiscoveredService service = DnssdServiceBrowser.ToService("key", "Odd", 1, ["id=XYZ", "v=one"], []);
        Assert.Equal(((string?)null, false, (int?)null), (service.IdPrefix, service.PairingOpen, service.ProtocolMajor));
    }

    /// <summary>
    /// Advertises a service from this PC with Windows' own DNS-SD and browses for it: proves the
    /// watcher query, properties and TXT parsing against the real OS stack. It is visible on the LAN
    /// while it runs, so it is gated.
    /// </summary>
    [Fact]
    public async Task The_browser_finds_a_service_advertised_on_this_PC()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_DNSSD_TESTS") == "1", "DNS-SD on the LAN: set EXTENDSPACE_DNSSD_TESTS=1 (advertises a test service on the local network for a few seconds)");

        const string Type = "_extendspace-test._tcp";
        string instance = $"ExtendSpace test {Environment.ProcessId}";
        using var listener = new StreamSocketListener();
        await listener.BindServiceNameAsync(string.Empty);
        int port = int.Parse(listener.Information.LocalPort, System.Globalization.CultureInfo.InvariantCulture);
        var advertised = new DnssdServiceInstance($"{instance}.{Type}.local", new HostName(Dns.GetHostName() + ".local"), (ushort)port);
        advertised.TextAttributes["id"] = "0123456789abcdef";
        advertised.TextAttributes["v"] = "0";
        DnssdRegistrationResult registration = await advertised.RegisterStreamSocketListenerAsync(listener);
        Assert.Equal(DnssdRegistrationStatus.Success, registration.Status);

        var found = new TaskCompletionSource<DiscoveredService>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        using var browser = new DnssdServiceBrowser(Type, lines.Add);
        browser.Found += service =>
        {
            if (service.InstanceName == instance && service.Port == port)
            {
                found.TrySetResult(service);
            }
        };
        browser.Start();

        DiscoveredService result = await found.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal("0123456789abcdef", result.IdPrefix);
        Assert.NotNull(WirelessDirectory.Choose(result));
        Assert.Empty(lines);
    }
}
