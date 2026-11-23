using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.ViewModels;

/// <summary>
/// The Connect screen's device list and choice (D65), against a scripted device list. No adb, no
/// device and no UI thread are involved: with no synchronization context the view model continues
/// on the test's own thread.
/// </summary>
public sealed class DeviceSelectionTests
{
    private const string Tablet = "852ec620";
    private const string Phone = "R58M12345";

    private readonly ScriptedDiscovery _discovery = new();
    private readonly HostDeviceSelection _selection = new();
    private readonly SessionStateMachine _session = new();

    [Fact]
    public async Task Before_the_first_listing_and_with_no_device_connect_is_disabled_and_says_what_to_do()
    {
        ConnectViewModel viewModel = Create();
        Assert.False(viewModel.CanConnect);
        Assert.Contains("Looking for", viewModel.DeviceHint);

        _discovery.Next = Listed();
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.HasDevices);
        Assert.False(viewModel.CanConnect);
        Assert.Contains("USB debugging", viewModel.DevicesStatus);
        Assert.Equal(viewModel.DevicesStatus, viewModel.DeviceHint);
        Assert.False(viewModel.Connect());
        Assert.Equal(SessionState.Disconnected, _session.State);
    }

    [Fact]
    public async Task The_only_usable_device_is_chosen_by_itself_and_handed_to_the_transport_when_the_session_starts()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "device", "Xiaomi Pad 7"));

        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Tablet, viewModel.SelectedDevice?.Serial);
        Assert.Equal("Xiaomi Pad 7", viewModel.SelectedDevice?.Title);
        Assert.Null(viewModel.DeviceHint);
        Assert.True(viewModel.CanConnect);
        Assert.Null(_selection.Serial);

        Assert.True(viewModel.Connect());

        Assert.Equal(Tablet, _selection.Serial);
        Assert.Equal(SessionPhase.Connecting, _session.State.Phase);
    }

    [Fact]
    public async Task With_several_devices_none_is_guessed_and_the_user_must_choose()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "device", "Xiaomi Pad 7"), new AdbDevice(Phone, "device", "Galaxy S10"));

        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.SelectedDevice);
        Assert.False(viewModel.CanConnect);
        Assert.Equal("Choose the device to connect to.", viewModel.DeviceHint);

        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Serial == Phone);
        Assert.True(viewModel.CanConnect);
        Assert.True(viewModel.Connect());
        Assert.Equal(Phone, _selection.Serial);
    }

    [Fact]
    public async Task An_unauthorised_or_offline_device_is_listed_but_cannot_be_connected_to_and_the_reason_is_shown()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "unauthorized"), new AdbDevice(Phone, "offline"));

        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.Devices.Count);
        Assert.Null(viewModel.SelectedDevice);
        Assert.Equal("No device is ready yet.", viewModel.DevicesStatus);
        Assert.False(viewModel.CanConnect);

        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Serial == Tablet);
        Assert.False(viewModel.CanConnect);
        Assert.Contains("Allow USB debugging", viewModel.DeviceHint);

        // Once the user allows it on the device, the next listing makes it usable, still chosen.
        _discovery.Next = Listed(new AdbDevice(Tablet, "device", "Xiaomi Pad 7"), new AdbDevice(Phone, "offline"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Tablet, viewModel.SelectedDevice?.Serial);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public async Task The_choice_is_kept_by_serial_across_refreshes_and_lost_when_its_device_goes_away()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "device", "Pad"), new AdbDevice(Phone, "device", "Phone"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Serial == Tablet);

        _discovery.Next = Listed(new AdbDevice(Phone, "device", "Phone"), new AdbDevice(Tablet, "device", "Pad"), new AdbDevice("third", "device", "Other"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Tablet, viewModel.SelectedDevice?.Serial);
        Assert.Same(viewModel.SelectedDevice, viewModel.Devices.Single(device => device.Serial == Tablet));

        // Unplugged while chosen, with others still attached: nothing is chosen in its place.
        _discovery.Next = Listed(new AdbDevice(Phone, "device", "Phone"), new AdbDevice("third", "device", "Other"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Null(viewModel.SelectedDevice);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task A_device_that_is_not_listed_cannot_be_chosen()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "device"), new AdbDevice(Phone, "device"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        viewModel.SelectedDevice = new DeviceOption("elsewhere", "Ghost", "Ready", true);

        Assert.Null(viewModel.SelectedDevice);
    }

    [Fact]
    public async Task Without_adb_the_list_says_so_and_connect_is_disabled()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = DeviceListResult.Failed(DeviceListStatus.AdbNotFound, "adb was not found. Install it.");

        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("adb was not found. Install it.", viewModel.DevicesStatus);
        Assert.Equal(viewModel.DevicesStatus, viewModel.DeviceHint);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task During_a_session_the_choice_is_fixed_and_a_refresh_does_not_move_it()
    {
        ConnectViewModel viewModel = Create();
        _discovery.Next = Listed(new AdbDevice(Tablet, "device"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.Connect());

        _discovery.Next = Listed(new AdbDevice(Phone, "device"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        viewModel.SelectedDevice = viewModel.Devices.Single();

        Assert.Equal(Tablet, viewModel.SelectedDevice?.Serial);
        Assert.Equal(Tablet, _selection.Serial);
    }

    [Fact]
    public async Task A_refresh_while_one_is_running_does_nothing()
    {
        ConnectViewModel viewModel = Create();
        var pending = new TaskCompletionSource<DeviceListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _discovery.Pending = pending.Task;

        Task first = viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _discovery.Calls);

        pending.SetResult(Listed(new AdbDevice(Tablet, "device")));
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(Tablet, viewModel.SelectedDevice?.Serial);

        _discovery.Pending = null;
        _discovery.Next = Listed(new AdbDevice(Tablet, "device"));
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _discovery.Calls);
    }

    [Fact]
    public async Task Choosing_wireless_does_not_need_a_usb_device()
    {
        var everything = new SessionCapabilities(Availability.Available, Availability.Available, Availability.Available, Availability.Available);
        var viewModel = new ConnectViewModel(new MachineSessionControl(_session), everything, _discovery, _selection)
        {
            SelectedTransport = TransportKind.Wireless,
        };
        _discovery.Next = Listed();
        await viewModel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        // No USB device is asked for; without wireless set up (D74) Connect is blocked and says why.
        Assert.Null(viewModel.DeviceHint);
        Assert.NotNull(viewModel.ConnectBlockedReason);
        Assert.False(viewModel.Connect());
        Assert.Null(_selection.Serial);
    }

    private ConnectViewModel Create() =>
        new(new MachineSessionControl(_session), SessionCapabilities.CurrentBuild, _discovery, _selection);

    private static DeviceListResult Listed(params AdbDevice[] devices) => new(DeviceListStatus.Listed, devices);

    private sealed class ScriptedDiscovery : IDeviceDiscovery
    {
        private int _calls;

        public DeviceListResult Next { get; set; } = new(DeviceListStatus.Listed, []);

        public Task<DeviceListResult>? Pending { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<DeviceListResult> ListAsync(CancellationToken cancellation)
        {
            Interlocked.Increment(ref _calls);
            return Pending ?? Task.FromResult(Next);
        }
    }
}
