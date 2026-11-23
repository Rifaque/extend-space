using ExtendSpace.Core.Display;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Sessions;
using ExtendSpace.Core.Tests.ViewModels;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.Display;

public sealed class ExtendStreamShapeTests
{
    private static ClientCapabilities Decoder(int maxWidth, int maxHeight) =>
        new("Pad", "1", 3200, 2136, 144, [new DecoderCapability("c2.avc", true, true, maxWidth, maxHeight)]);

    [Theory]
    [InlineData(2560, 1440, 1920, 1080)]   // 16:9: exactly D16's stream
    [InlineData(2560, 1600, 1728, 1080)]   // 16:10
    [InlineData(2400, 1600, 1620, 1080)]   // 3:2, the Pad 7's shape
    [InlineData(1600, 2560, 1080, 1728)]   // portrait: turned envelope
    [InlineData(1280, 800, 1280, 800)]     // smaller than the envelope: its own size
    [InlineData(1281, 801, 1280, 800)]     // odd sizes become even
    [InlineData(3440, 1440, 1920, 802)]    // ultrawide
    public void An_extended_display_is_streamed_in_its_own_shape_inside_the_measured_envelope(int width, int height, int expectedWidth, int expectedHeight)
    {
        EncodingRequest? chosen = StreamConfigurationPolicy.ChooseShaped(new VideoSourceDescription(width, height, 60), Decoder(4096, 4096));
        Assert.Equal(new EncodingRequest(expectedWidth, expectedHeight, 60, 12_000_000), chosen);
    }

    [Fact]
    public void When_the_decoder_cannot_take_the_preferred_shape_the_720p_envelope_is_used_and_otherwise_nothing()
    {
        Assert.Equal(new EncodingRequest(1152, 720, 30, 6_000_000), StreamConfigurationPolicy.ChooseShaped(new VideoSourceDescription(2560, 1600, 60), Decoder(1280, 720)));
        Assert.Null(StreamConfigurationPolicy.ChooseShaped(new VideoSourceDescription(2560, 1600, 60), Decoder(640, 480)));
    }

    [Fact]
    public void Only_an_Extend_session_uses_the_shaped_choice_so_Mirror_keeps_D16()
    {
        int mirror = 0, extend = 0;
        var options = new SessionCoordinatorOptions
        {
            ChooseEncoding = (source, client) => { mirror++; return StreamConfigurationPolicy.Choose(source, client); },
            ChooseExtendEncoding = (source, client) => { extend++; return StreamConfigurationPolicy.ChooseShaped(source, client); },
        };

        var mirrorHarness = new CoordinatorHarness(options);
        Assert.Equal(SessionStartResult.Started, mirrorHarness.Coordinator.Start(CoordinatorHarness.UsbMirror));
        var extendHarness = new CoordinatorHarness(options);
        Assert.Equal(SessionStartResult.Started, extendHarness.Coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Extend, MonitorDeviceName: @"\\.\DISPLAY12")));

        Assert.Equal((1, 1), (mirror, extend));
        Assert.Equal(SessionPhase.Streaming, extendHarness.Phase);
    }
}

public sealed class ExtendProtocolTests
{
    [Fact]
    public void Stream_config_says_extend_only_for_an_extended_display_and_old_messages_read_as_mirror()
    {
        var config = new StreamConfigMessage("h264", "high", 1728, 1080, 60, 12_000_000, 1 << 20, null, null);
        byte[] mirror = ControlCodec.Encode(config);
        byte[] extend = ControlCodec.Encode(config with { Mode = "extend" });

        Assert.Null(Assert.IsType<StreamConfigMessage>(ControlCodec.Decode(mirror[1], mirror.AsSpan(WireFormat.HeaderLength))).Mode);
        Assert.Equal("extend", Assert.IsType<StreamConfigMessage>(ControlCodec.Decode(extend[1], extend.AsSpan(WireFormat.HeaderLength))).Mode);
        Assert.DoesNotContain("mode", System.Text.Encoding.UTF8.GetString(mirror.AsSpan(WireFormat.HeaderLength)));
    }
}

public sealed class ExtendViewModelTests
{
    private sealed class FixedCatalog(params DisplayTarget[] targets) : IVirtualDisplayCatalog
    {
        public DisplayTarget[] Current { get; set; } = targets;

        public int Reads { get; private set; }

        public IReadOnlyList<DisplayTarget> Targets()
        {
            Reads++;
            return Current;
        }
    }

    private sealed class Monitors(params DisplayInfo[] displays) : IDisplayCatalog
    {
        public IReadOnlyList<DisplayInfo> List() => displays;
    }

    private static readonly SessionCapabilities Everything = new(Availability.Available, Availability.Available, Availability.Available, Availability.Available);
    private readonly SessionStateMachine _session = new();
    private readonly HostDeviceSelection _selection = new();

    private ConnectViewModel Create(FixedCatalog catalog) => new(
        new MachineSessionControl(_session),
        Everything,
        selection: _selection,
        displays: new Monitors(new DisplayInfo(@"\\.\DISPLAY5", true, 2560, 1440, 165), new DisplayInfo(@"\\.\DISPLAY12", false, 2400, 1600, 60)),
        virtualDisplays: catalog);

    [Fact]
    public void Without_the_driver_Extend_is_blocked_with_install_guidance_and_Mirror_is_unaffected()
    {
        ConnectViewModel viewModel = Create(new FixedCatalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0)));
        viewModel.RefreshDisplays();
        Assert.True(viewModel.CanConnect);

        viewModel.SelectedMode = DisplayMode.Extend;

        Assert.False(viewModel.CanConnect);
        Assert.Contains("Virtual Display Driver", viewModel.ConnectBlockedReason, StringComparison.Ordinal);
        Assert.True(viewModel.ExtendIsProblem);
        Assert.False(viewModel.ShowsDisplayChoice);
        Assert.True(viewModel.ShowsExtendStatus);
    }

    [Fact]
    public void A_virtual_display_outside_the_desktop_asks_for_Display_settings()
    {
        ConnectViewModel viewModel = Create(new FixedCatalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0), VirtualDisplayDetectorTests.Vdd(null, active: false)));
        viewModel.SelectedMode = DisplayMode.Extend;

        Assert.False(viewModel.CanConnect);
        Assert.Contains("Extend these displays", viewModel.ExtendStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ready_virtual_display_is_the_session_source_and_input_target_and_is_not_offered_for_Mirror()
    {
        var catalog = new FixedCatalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0), VirtualDisplayDetectorTests.Vdd(@"\\.\DISPLAY12"));
        ConnectViewModel viewModel = Create(catalog);
        viewModel.RefreshDisplays();
        Assert.DoesNotContain(viewModel.Displays, display => display.DeviceName == @"\\.\DISPLAY12");

        viewModel.SelectedMode = DisplayMode.Extend;
        Assert.True(viewModel.ExtendIsReady);
        Assert.Contains("Display 12, 2400 × 1600", viewModel.ExtendStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.Connect());

        Assert.Equal(new SessionRequest(TransportKind.Usb, DisplayMode.Extend, MonitorDeviceName: @"\\.\DISPLAY12"), _session.State.Request);
        Assert.Equal((@"\\.\DISPLAY12", DisplayMode.Extend), (_selection.MonitorDeviceName, _selection.Mode));
    }

    [Fact]
    public void The_status_follows_the_display_configuration_while_a_choice_can_be_made_and_is_fixed_during_a_session()
    {
        var catalog = new FixedCatalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0));
        ConnectViewModel viewModel = Create(catalog);
        viewModel.SelectedMode = DisplayMode.Extend;
        Assert.False(viewModel.ExtendIsReady);

        catalog.Current = [VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0), VirtualDisplayDetectorTests.Vdd(@"\\.\DISPLAY12")];
        viewModel.RefreshDisplays();
        Assert.True(viewModel.ExtendIsReady);
        Assert.True(viewModel.Connect());

        int reads = catalog.Reads;
        catalog.Current = [];
        viewModel.RefreshDisplays();
        Assert.Equal(reads, catalog.Reads);
        Assert.True(viewModel.ExtendIsReady);
    }

    [Fact]
    public void Mirror_still_mirrors_the_chosen_physical_display_with_the_mode_recorded()
    {
        ConnectViewModel viewModel = Create(new FixedCatalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0), VirtualDisplayDetectorTests.Vdd(@"\\.\DISPLAY12")));
        viewModel.RefreshDisplays();
        Assert.True(viewModel.Connect());
        Assert.Equal((null, DisplayMode.Mirror), (_selection.MonitorDeviceName, _selection.Mode));
    }
}

public sealed class ExtendInputTargetingTests
{
    private sealed class Locator : ExtendSpace.Core.Input.IMonitorLocator
    {
        public string? AskedFor { get; private set; }

        public ExtendSpace.Core.Input.ScreenRect? Locate(string? deviceName)
        {
            AskedFor = deviceName;
            return deviceName == @"\.\DISPLAY12" ? new ExtendSpace.Core.Input.ScreenRect(2560, 0, 2400, 1600) : new ExtendSpace.Core.Input.ScreenRect(0, 0, 2560, 1440);
        }
    }

    [Fact]
    public void Touches_land_inside_the_virtual_monitor_and_even_clamped_never_on_a_physical_one()
    {
        var locator = new Locator();
        var touch = new ExtendSpace.Core.Tests.Input.RecordingTouch();
        using var input = new ExtendSpace.Core.Input.HostInput(locator, () => touch, new ExtendSpace.Core.Tests.Input.RecordingMouse(), () => @"\.\DISPLAY12", autoTick: false) { Mode = ExtendSpace.Core.Input.InputMode.Touch };
        object stream = new();
        input.BeginStream(stream, 1620, 1080);

        uint sequence = 0;
        foreach ((ushort x, ushort y) in new (ushort, ushort)[] { (32768, 32768), (0, 0), (65535, 65535) })
        {
            input.Handle(stream, new ExtendSpace.Core.Input.TouchFrame(sequence++, [new ExtendSpace.Core.Input.TouchContact(0, ExtendSpace.Core.Input.TouchPhase.Down, x, y)]));
            input.Handle(stream, new ExtendSpace.Core.Input.TouchFrame(sequence++, [new ExtendSpace.Core.Input.TouchContact(0, ExtendSpace.Core.Input.TouchPhase.Up, x, y)]));
        }

        Assert.Equal(@"\.\DISPLAY12", locator.AskedFor);
        Assert.Equal("0:Down@3760,800", ExtendSpace.Core.Tests.Input.RecordingTouch.Describe(touch.Frames[0]));
        Assert.All(touch.Frames.SelectMany(frame => frame), contact =>
        {
            Assert.InRange(contact.Point.X, 2560, 2560 + 2400 - 1);
            Assert.InRange(contact.Point.Y, 0, 1600 - 1);
        });
    }
}
