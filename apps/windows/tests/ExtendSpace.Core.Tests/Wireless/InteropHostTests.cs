using System.Net;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Tests.Input;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.Tests.Wireless;

/// <summary>
/// The PC's half of the cross-platform run on one PC: the real Windows pairing, Wi-Fi transport,
/// connection controller and input path against the Android app's real endpoint and link running in
/// a JVM (<c>InteropTabletTest</c>). Gated by <c>EXTENDSPACE_INTEROP_DIR</c>, shared by both.
/// </summary>
public sealed class InteropHostTests
{
    [Fact]
    public async Task Pair_with_the_Android_endpoint_then_stream_and_receive_a_touch()
    {
        string? folder = Environment.GetEnvironmentVariable("EXTENDSPACE_INTEROP_DIR");
        Assert.SkipUnless(folder is not null, "cross-platform interop: set EXTENDSPACE_INTEROP_DIR and run InteropTabletTest at the same time");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string Read(string name) => File.ReadAllText(Path.Combine(folder!, name)).Trim();
        async Task WaitForFileAsync(string name)
        {
            long deadline = Environment.TickCount64 + 120_000;
            while (!File.Exists(Path.Combine(folder!, name)))
            {
                Assert.True(Environment.TickCount64 < deadline, $"{name} never appeared");
                await Task.Delay(50, cancel);
            }
        }

        await WaitForFileAsync("port");
        int port = int.Parse(Read("port"), System.Globalization.CultureInfo.InvariantCulture);
        var endPoint = new IPEndPoint(IPAddress.Loopback, port);
        HostIdentity host = HostIdentity.Create();
        var trust = new TrustedDeviceStore(Path.Combine(folder!, "host-trust.json"));

        // 1. Pairing: the code shown here must be the one the tablet showed.
        string? shown = null;
        PairingResult paired = await new WirelessPairing(host, trust, new PairingOptions { DeviceName = "Interop PC", DecisionTimeout = TimeSpan.FromSeconds(60) })
            .PairAsync(endPoint, "Interop Pad", (code, _, _) => { shown = code; return Task.FromResult(true); }, cancel);
        Assert.Equal(PairingStatus.Paired, paired.Status);
        await WaitForFileAsync("tablet-code");
        Assert.Equal(Read("tablet-code"), shown);
        Assert.Equal(Read("tablet-id"), paired.Device!.DeviceId);

        // 2. A session over Wi-Fi TLS, with touch on.
        await WaitForFileAsync("session-ready");
        var directory = new WirelessDirectory(trust);
        directory.Upsert(new DiscoveredService("interop", "Interop Pad", paired.Device.DeviceId[..16], [IPAddress.Loopback], port, false, 0));
        var selection = new HostDeviceSelection { Transport = TransportKind.Wireless, WirelessDeviceId = paired.Device.DeviceId };
        var touch = new RecordingTouch();
        using var input = new HostInput(new FixedMonitor(new ScreenRect(0, 0, 1920, 1080)), () => touch, new RecordingMouse(), () => null, autoTick: false) { Mode = InputMode.Touch };
        var controller = new HostConnectionController(
            new RoutedTransport(selection, new LoopbackTransport(), new WirelessTransport(() => host, trust, directory, () => selection.WirelessDeviceId)),
            new HostConnectionOptions { Input = input, AppVersion = "0.2.0-interop" });

        Assert.Equal(OutcomeKind.Succeeded, (await controller.OpenAsync(cancel)).Kind);
        AttemptOutcome<ClientCapabilities> capabilities = await controller.ExchangeCapabilitiesAsync(cancel);
        Assert.Equal(("Interop Pad", 2560), (capabilities.Value!.DeviceName, capabilities.Value.DisplayWidth));
        Assert.Equal(OutcomeKind.Succeeded, (await controller.ConfigureStreamAsync(new StreamSettings(1920, 1080, 60, 12_000_000, "Interop Encoder", true), cancel)).Kind);

        long deadline = Environment.TickCount64 + 20_000;
        while (touch.Frames.Count < 2)
        {
            controller.OfferVideo(H264Samples.IdrAccessUnit, Environment.TickCount64 * 1000);
            Assert.True(Environment.TickCount64 < deadline, "no touch arrived from the tablet");
            await Task.Delay(50, cancel);
        }

        Assert.Equal(["0:Down@960,540", "0:Up@960,540"], touch.Frames.Take(2).Select(RecordingTouch.Describe));
        await controller.CloseAsync("interop over", null, cancel);

        await WaitForFileAsync("tablet-result");
        Assert.Contains("paired=true", Read("tablet-result"), StringComparison.Ordinal);
        Assert.Contains("transport=WIRELESS", Read("tablet-result"), StringComparison.Ordinal);
        Assert.Contains("end=PEER_ENDED", Read("tablet-result"), StringComparison.Ordinal);
    }
}
