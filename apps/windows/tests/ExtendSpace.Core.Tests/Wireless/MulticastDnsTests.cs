using System.Net;
using System.Text;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.Tests.Wireless;

public sealed class MulticastDnsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A response shaped like Android's advertiser: PTR answer, SRV/TXT/A additionals, compressed names.</summary>
    internal static byte[] Response(uint ptrTtl = 4500, string instance = "Xiaomi 2410CRP4CI", string txtId = "id=852ec620a1b2c3d4")
    {
        var m = new List<byte>();
        m.AddRange(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 3 });
        int serviceAt = m.Count;
        Name(m, "_extendspace", "_tcp", "local");
        Record(m, 12, ptrTtl, rdata =>
        {
            Label(rdata, instance);
            rdata.AddRange(new byte[] { 0xC0, (byte)serviceAt });
        });
        int instanceAt = m.Count;
        Label(m, instance);
        m.AddRange(new byte[] { 0xC0, (byte)serviceAt });
        Record(m, 33, 120, rdata =>
        {
            rdata.AddRange(new byte[] { 0, 0, 0, 0, 0x6A, 0x30 }); // priority, weight, port 27184
            Name(rdata, "Android_abc", "local");
        });
        m.AddRange(new byte[] { 0xC0, (byte)instanceAt });
        Record(m, 16, 4500, rdata =>
        {
            foreach (string t in new[] { txtId, "p=1", "v=1" })
            {
                rdata.Add((byte)t.Length);
                rdata.AddRange(Encoding.ASCII.GetBytes(t));
            }
        });
        Name(m, "Android_abc", "local");
        Record(m, 1, 120, rdata => rdata.AddRange(new byte[] { 192, 168, 1, 193 }));
        return [.. m];
    }

    private static void Label(List<byte> m, string label)
    {
        m.Add((byte)Encoding.UTF8.GetByteCount(label));
        m.AddRange(Encoding.UTF8.GetBytes(label));
    }

    private static void Name(List<byte> m, params string[] labels)
    {
        foreach (string label in labels)
        {
            Label(m, label);
        }

        m.Add(0);
    }

    private static void Record(List<byte> m, ushort type, uint ttl, Action<List<byte>> data)
    {
        var rdata = new List<byte>();
        data(rdata);
        m.AddRange(new[] { (byte)(type >> 8), (byte)type, (byte)0x80, (byte)1 });
        m.AddRange(new[] { (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl });
        m.AddRange(new[] { (byte)(rdata.Count >> 8), (byte)rdata.Count });
        m.AddRange(rdata);
    }

    [Fact]
    public void A_compressed_Android_style_response_decodes_to_its_records()
    {
        IReadOnlyList<DnsRecord> records = DnsMessage.Records(Response());

        Assert.Equal("Xiaomi 2410CRP4CI._extendspace._tcp.local", records[0].Target);
        DnsRecord srv = Assert.Single(records, r => r.Type == DnsRecord.Srv);
        Assert.Equal(27184, srv.Port);
        Assert.Equal("Android_abc.local", srv.Target);
        Assert.Equal(["id=852ec620a1b2c3d4", "p=1", "v=1"], Assert.Single(records, r => r.Type == DnsRecord.Txt).Text);
        Assert.Equal(IPAddress.Parse("192.168.1.193"), Assert.Single(records, r => r.Type == DnsRecord.A).Address);
    }

    [Fact]
    public void The_browser_reports_the_service_with_address_port_and_TXT_fields()
    {
        using var browser = new MulticastDnsBrowser();
        IReadOnlyList<DiscoveredService> found = browser.Apply(DnsMessage.Records(Response()), Now);

        DiscoveredService service = Assert.Single(found);
        Assert.Equal("Xiaomi 2410CRP4CI", service.InstanceName);
        Assert.Equal("852ec620a1b2c3d4", service.IdPrefix);
        Assert.True(service.PairingOpen);
        Assert.Equal(1, service.ProtocolMajor);
        Assert.Equal([IPAddress.Parse("192.168.1.193")], service.Addresses);
        Assert.Equal(27184, service.Port);

        // The same answer again changes nothing.
        Assert.Empty(browser.Apply(DnsMessage.Records(Response()), Now));
    }

    [Fact]
    public void A_goodbye_and_an_expired_TTL_both_report_the_service_lost()
    {
        using var browser = new MulticastDnsBrowser();
        var lost = new List<string>();
        browser.Lost += lost.Add;
        browser.Apply(DnsMessage.Records(Response()), Now);

        browser.Apply(DnsMessage.Records(Response(ptrTtl: 0)), Now);
        Assert.Equal(["Xiaomi 2410CRP4CI._extendspace._tcp.local"], lost);

        browser.Apply(DnsMessage.Records(Response(ptrTtl: 10)), Now);
        browser.Expire(Now.AddSeconds(11));
        Assert.Equal(2, lost.Count);
    }

    [Fact]
    public void A_PTR_without_details_asks_for_SRV_and_TXT_next()
    {
        using var browser = new MulticastDnsBrowser();
        byte[] full = Response();
        // Keep only the header and the PTR answer: zero the additional count.
        full[11] = 0;
        browser.Apply(DnsMessage.Records(full), Now);

        Assert.Equal([("Xiaomi 2410CRP4CI._extendspace._tcp.local", DnsRecord.Srv), ("Xiaomi 2410CRP4CI._extendspace._tcp.local", DnsRecord.Txt)], browser.Missing());
    }

    [Fact]
    public void A_query_encodes_its_names_and_types()
    {
        byte[] query = DnsMessage.Query([("_extendspace._tcp.local", DnsRecord.Ptr)]);
        Assert.Equal(1, query[5]);
        Assert.Equal("_extendspace", Encoding.ASCII.GetString(query, 13, 12));
        Assert.Equal(new byte[] { 0, 12, 0, 1 }, query[^4..]);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0x84, 0 })]
    [InlineData(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12 })]
    [InlineData(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 5, 0x61 })]
    public void Malformed_messages_are_refused_not_crashed_on(byte[] message)
    {
        Assert.Throws<FormatException>(() => DnsMessage.Records(message));
    }

    /// <summary>The real tablet, waiting on Wi-Fi. Gated: EXTENDSPACE_MDNS_TESTS=1.</summary>
    [Fact]
    public async Task The_waiting_tablet_is_found_on_the_real_network()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_MDNS_TESTS") == "1", "needs a tablet waiting on the LAN: set EXTENDSPACE_MDNS_TESTS=1");
        var found = new TaskCompletionSource<DiscoveredService>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var browser = new MulticastDnsBrowser(log: line => TestContext.Current.SendDiagnosticMessage(line));
        browser.Found += service => found.TrySetResult(service);
        browser.Start();
        DiscoveredService service = await found.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"MDNS found '{service.InstanceName}' {string.Join(',', service.Addresses)}:{service.Port} id={service.IdPrefix} pairing={service.PairingOpen}");
        Assert.NotEmpty(service.Addresses);
    }
}
