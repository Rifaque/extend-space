using System.Net;
using System.Security.Cryptography;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Tests.ViewModels;
using ExtendSpace.Core.ViewModels;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.Tests.Wireless;

public sealed class WirelessViewModelTests : IDisposable
{
    private static readonly SessionCapabilities Everything = new(Availability.Available, Availability.Available, Availability.Available, Availability.Available);

    private readonly TempFolder _folder = new();
    private readonly FakeTablet _tablet = new();
    private readonly WirelessHost _wireless;
    private readonly HostDeviceSelection _selection = new();
    private readonly SessionStateMachine _session = new();

    public WirelessViewModelTests()
    {
        _wireless = new WirelessHost(
            new HostIdentityStore(_folder.File("identity.bin"), new XorProtector()),
            new TrustedDeviceStore(_folder.File("trusted.json")),
            browser: null,
            new PairingOptions { DecisionTimeout = TimeSpan.FromSeconds(5) });
    }

    public void Dispose()
    {
        _tablet.Dispose();
        _folder.Dispose();
    }

    private ConnectViewModel Create() => new(new MachineSessionControl(_session), Everything, selection: _selection, wireless: _wireless)
    {
        SelectedTransport = TransportKind.Wireless,
    };

    private void Advertise(bool pairingOpen = true) =>
        _wireless.Directory.Upsert(new DiscoveredService("k", "Fake Pad", _tablet.Identity.DeviceId[..16], [IPAddress.Loopback], _tablet.Port, pairingOpen, 0));

    [Fact]
    public void With_nothing_found_the_list_says_how_to_make_the_tablet_visible()
    {
        ConnectViewModel viewModel = Create();

        Assert.Empty(viewModel.Devices);
        Assert.Contains("Wi-Fi connections", viewModel.DevicesStatus, StringComparison.Ordinal);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void An_unpaired_tablet_can_be_paired_but_not_connected_to()
    {
        ConnectViewModel viewModel = Create();
        Advertise();
        viewModel.RefreshWirelessDevices();
        viewModel.SelectedDevice = Assert.Single(viewModel.Devices);

        Assert.Equal("Not paired · ready to pair", viewModel.SelectedDevice!.Status);
        Assert.Contains("Pair with Fake Pad first", viewModel.DeviceHint, StringComparison.Ordinal);
        Assert.True(viewModel.CanPair);
        Assert.False(viewModel.CanForget);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task Pairing_from_the_screen_trusts_the_tablet_then_Connect_hands_its_ID_to_the_session()
    {
        ConnectViewModel viewModel = Create();
        Advertise();
        viewModel.RefreshWirelessDevices();
        viewModel.SelectedDevice = Assert.Single(viewModel.Devices);
        Task tablet = AcceptPairingAsync(accept: true);
        string? shown = null;

        PairingResult? result = await viewModel.PairSelectedAsync((code, _, _) => { shown = code; return Task.FromResult(true); }, TestContext.Current.CancellationToken).Within();
        await tablet.Within();

        Assert.Equal(PairingStatus.Paired, result!.Status);
        Assert.Equal(6, shown!.Length);
        Assert.Equal("Paired · ready", viewModel.SelectedDevice!.Status);
        Assert.Null(viewModel.DeviceHint);
        Assert.True(viewModel.CanConnect);
        Assert.True(viewModel.CanForget);

        Assert.True(viewModel.Connect());
        Assert.Equal((TransportKind.Wireless, _tablet.Identity.DeviceId, (string?)null), (_selection.Transport, _selection.WirelessDeviceId, _selection.Serial));
    }

    [Fact]
    public async Task A_tablet_added_by_address_can_be_paired_and_is_then_ready()
    {
        ConnectViewModel viewModel = Create();
        Assert.True(viewModel.ShowsManualAddress);
        Assert.False(viewModel.AddWirelessAddress("not an address"));
        Assert.NotNull(viewModel.ManualAddressError);

        Assert.True(viewModel.AddWirelessAddress($"127.0.0.1:{_tablet.Port}"));
        Assert.Null(viewModel.ManualAddressError);
        Assert.Equal("Added by address · not paired", viewModel.SelectedDevice!.Status);
        Assert.True(viewModel.CanPair);

        Task tablet = AcceptPairingAsync(accept: true);
        PairingResult? result = await viewModel.PairSelectedAsync((_, _, _) => Task.FromResult(true), TestContext.Current.CancellationToken).Within();
        await tablet.Within();

        Assert.Equal(PairingStatus.Paired, result!.Status);
        Assert.Equal("Paired · ready", Assert.Single(viewModel.Devices).Status);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public void Forgetting_a_paired_tablet_removes_the_trust_and_requires_pairing_again()
    {
        _wireless.Trust.Add(new TrustedDevice(_tablet.Identity.DeviceId, "My Pad", DateTimeOffset.UnixEpoch));
        ConnectViewModel viewModel = Create();
        Advertise(pairingOpen: false);
        viewModel.RefreshWirelessDevices();
        Assert.True(viewModel.CanConnect);

        Assert.True(viewModel.ForgetSelected());

        Assert.Empty(_wireless.Trust.List());
        Assert.Contains("Forgot My Pad", viewModel.PairingStatus, StringComparison.Ordinal);
        Assert.Equal("Not paired", viewModel.SelectedDevice!.Status);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void A_paired_tablet_that_is_not_found_explains_what_to_do_on_the_tablet()
    {
        _wireless.Trust.Add(new TrustedDevice(new string('a', 64), "Away Pad", DateTimeOffset.UnixEpoch));
        ConnectViewModel viewModel = Create();
        viewModel.RefreshWirelessDevices();
        viewModel.SelectedDevice = Assert.Single(viewModel.Devices);

        Assert.Equal("Paired · not found on the network", viewModel.SelectedDevice!.Status);
        Assert.Contains("is not on the network", viewModel.DeviceHint, StringComparison.Ordinal);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void Switching_back_to_USB_shows_the_USB_list_and_does_not_hand_a_tablet_ID_to_a_USB_session()
    {
        _wireless.Trust.Add(new TrustedDevice(_tablet.Identity.DeviceId, "My Pad", DateTimeOffset.UnixEpoch));
        ConnectViewModel viewModel = Create();
        Advertise(pairingOpen: false);
        viewModel.RefreshWirelessDevices();
        Assert.Single(viewModel.Devices);

        viewModel.SelectedTransport = TransportKind.Usb;
        Assert.Empty(viewModel.Devices);
        Assert.True(viewModel.Connect());
        Assert.Equal((TransportKind.Usb, (string?)null), (_selection.Transport, _selection.WirelessDeviceId));
    }

    private async Task AcceptPairingAsync(bool accept)
    {
        (TestClient client, _, string hostId) = await _tablet.AcceptAsync();
        using (client)
        {
            await client.ExpectAsync<HelloMessage>();
            await client.SendHelloAsync("Fake Pad");
            await client.ExpectAsync<PairCommitMessage>();
            await client.SendAsync(new PairNonceMessage(RandomNumberGenerator.GetBytes(32)));
            await client.ExpectAsync<PairRevealMessage>();
            await client.SendAsync(new PairResultMessage(accept));
            await client.ReadUntilClosedAsync();
            Assert.True(DeviceIds.IsValid(hostId));
        }
    }
}
