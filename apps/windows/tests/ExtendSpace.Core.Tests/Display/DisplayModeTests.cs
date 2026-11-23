using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.ViewModels;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.Display;

public sealed class TabletModeAdvisorTests
{
    /// <summary>What VDD 25.7.23 offered on the development PC, read with EnumDisplaySettings (2026-11-23).</summary>
    internal static readonly DisplayModeSpec[] VddDefaults =
    [
        .. new[] { (800, 600), (1366, 768), (1920, 1080), (2560, 1440), (3840, 2160) }
            .SelectMany(size => new[] { 30, 60, 90, 120, 144, 165, 244 }.Select(hz => new DisplayModeSpec(size.Item1, size.Item2, hz))),
    ];

    /// <summary>The Xiaomi Pad 7 as its capabilities message reports it.</summary>
    internal static readonly TabletScreen Pad7 = new(3200, 2136, 144);

    [Fact]
    public void For_the_Pad_7_and_the_drivers_defaults_1920x1080_at_60_Hz_is_recommended_over_800x600_at_30()
    {
        ModeAdvice advice = TabletModeAdvisor.Advise(VddDefaults, new DisplayModeSpec(800, 600, 30), Pad7);

        Assert.Equal(new DisplayModeSpec(1920, 1080, 60), advice.Recommended!.Mode);
        Assert.False(advice.CurrentIsRecommended);
        Assert.Contains("1920 × 1080 at 60 Hz would suit this tablet better than 800 × 600 at 30 Hz", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_3_by_2_mode_the_advice_names_the_largest_unscaled_size_of_the_tablets_shape()
    {
        ModeAdvice advice = TabletModeAdvisor.Advise(VddDefaults, new DisplayModeSpec(1920, 1080, 60), Pad7);

        Assert.True(advice.CurrentIsRecommended);
        Assert.Equal(new DisplayModeSpec(1616, 1080, 60), advice.CustomSuggestion);
        Assert.Contains("adding 1616 × 1080", advice.Message, StringComparison.Ordinal);
        Assert.Equal(new DisplayModeSpec(1620, 1080, 60), TabletModeAdvisor.CustomSuggestion(new TabletScreen(2400, 1600, 60)));
    }

    [Fact]
    public void A_mode_of_the_tablets_shape_fills_it_and_wins_when_the_driver_offers_one()
    {
        DisplayModeSpec[] modes = [.. VddDefaults, new DisplayModeSpec(1616, 1080, 60), new DisplayModeSpec(2400, 1600, 60)];

        ModeAdvice advice = TabletModeAdvisor.Advise(modes, new DisplayModeSpec(1920, 1080, 60), Pad7);

        // 2400 x 1600 fills the screen too but would be scaled down to the stream envelope.
        Assert.Equal(new DisplayModeSpec(1616, 1080, 60), advice.Recommended!.Mode);
        Assert.False(advice.CurrentIsRecommended);
        Assert.Equal(1.0, advice.Recommended.Coverage, 2);
    }

    [Theory]
    [InlineData(2560, 1440, 0.75)]
    [InlineData(3840, 2160, 0.5)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1366, 768, 1.0)]
    public void Modes_larger_than_the_stream_envelope_lose_sharpness(int width, int height, double sharpness)
    {
        Assert.Equal(sharpness, TabletModeAdvisor.Score(new DisplayModeSpec(width, height, 60), Pad7).Sharpness, 3);
    }

    [Fact]
    public void Sixty_hertz_is_preferred_then_faster_then_slower()
    {
        DisplayModeSpec[] modes = [new(1920, 1080, 30), new(1920, 1080, 144), new(1920, 1080, 60)];
        Assert.Equal(60, TabletModeAdvisor.Advise(modes, null, Pad7).Recommended!.Mode.RefreshHz);
        Assert.Equal(144, TabletModeAdvisor.Advise([.. modes.Where(m => m.RefreshHz != 60)], null, Pad7).Recommended!.Mode.RefreshHz);
        Assert.Equal(30, TabletModeAdvisor.Advise([new(1920, 1080, 30)], null, Pad7).Recommended!.Mode.RefreshHz);
    }

    [Fact]
    public void A_portrait_tablet_report_is_judged_by_its_landscape_shape()
    {
        Assert.Equal(TabletModeAdvisor.Score(new DisplayModeSpec(1920, 1080, 60), Pad7), TabletModeAdvisor.Score(new DisplayModeSpec(1920, 1080, 60), new TabletScreen(2136, 3200, 144)));
    }

    [Fact]
    public void An_unknown_tablet_is_advised_on_sharpness_and_room_only()
    {
        ModeAdvice advice = TabletModeAdvisor.Advise(VddDefaults, new DisplayModeSpec(800, 600, 30), null);
        Assert.Equal(new DisplayModeSpec(1920, 1080, 60), advice.Recommended!.Mode);
        Assert.Null(advice.CustomSuggestion);
    }

    [Fact]
    public void No_modes_gives_no_recommendation()
    {
        Assert.Null(TabletModeAdvisor.Advise([], null, Pad7).Recommended);
    }
}

public sealed class WindowRescuePlanTests
{
    private static readonly ScreenRect Virtual = new(2560, 0, 800, 600);
    private static readonly ScreenRect Main = new(0, 0, 2560, 1440);

    [Fact]
    public void A_window_on_the_virtual_display_keeps_its_offset_on_the_main_display()
    {
        Assert.Equal(new ScreenPoint(50, 30), WindowRescuePlan.Destination(new ScreenRect(2610, 30, 400, 300), Virtual, Main));
    }

    [Fact]
    public void A_window_whose_centre_is_elsewhere_is_left_alone()
    {
        Assert.Null(WindowRescuePlan.Destination(new ScreenRect(2000, 100, 800, 600), Virtual, Main));
        Assert.Null(WindowRescuePlan.Destination(new ScreenRect(-1000, 100, 800, 600), Virtual, Main));
    }

    [Fact]
    public void A_window_hanging_off_the_virtual_display_lands_inside_the_main_display()
    {
        Assert.Equal(new ScreenPoint(0, 0), WindowRescuePlan.Destination(new ScreenRect(2500, -20, 500, 300), Virtual, Main));
        Assert.Equal(new ScreenPoint(0, 0), WindowRescuePlan.Destination(new ScreenRect(1460, -700, 3000, 2000), Virtual, Main));
    }
}

public sealed class DisplayModeViewModelTests
{
    private sealed class Catalog(params DisplayTarget[] targets) : IVirtualDisplayCatalog
    {
        public IReadOnlyList<DisplayTarget> Targets() => targets;
    }

    private sealed class Modes(DisplayModeSpec current) : IVirtualDisplayModes
    {
        public DisplayModeSpec Now { get; private set; } = current;

        public List<(string Device, DisplayModeSpec Mode)> Applied { get; } = [];

        public IReadOnlyList<DisplayModeSpec> Available(string deviceName) => TabletModeAdvisorTests.VddDefaults;

        public DisplayModeSpec? Current(string deviceName) => Now;

        public string? Apply(string deviceName, DisplayModeSpec mode)
        {
            Applied.Add((deviceName, mode));
            Now = mode;
            return null;
        }
    }

    private sealed class Rescue : IWindowRescue
    {
        public List<string> Calls { get; } = [];

        public int BringBack(string virtualDeviceName)
        {
            Calls.Add(virtualDeviceName);
            return 2;
        }
    }

    private static readonly SessionCapabilities Everything = new(Availability.Available, Availability.Available, Availability.Available, Availability.Available);
    private readonly SessionStateMachine _session = new();
    private readonly Modes _modes = new(new DisplayModeSpec(800, 600, 30));
    private readonly Rescue _rescue = new();

    private ConnectViewModel Create(ClientCapabilities? client) => new(
        new MachineSessionControl(_session),
        Everything,
        selection: new HostDeviceSelection(),
        virtualDisplays: new Catalog(VirtualDisplayDetectorTests.Physical(@"\\.\DISPLAY5", 0), VirtualDisplayDetectorTests.Vdd(@"\\.\DISPLAY9", width: 800, height: 600)),
        virtualDisplayModes: _modes,
        windowRescue: _rescue,
        lastClient: () => client);

    [Fact]
    public void The_recommended_mode_is_offered_and_applied_to_the_virtual_display_only()
    {
        ConnectViewModel viewModel = Create(new ClientCapabilities("Pad 7", "0.2.0", 3200, 2136, 144, []));
        viewModel.SelectedMode = DisplayMode.Extend;
        viewModel.RefreshExtendStatus();

        Assert.True(viewModel.ShowsModeAdvice);
        Assert.True(viewModel.CanApplyRecommendedMode);
        Assert.Equal("Use 1920 × 1080 at 60 Hz", viewModel.ApplyRecommendedModeLabel);

        viewModel.ApplyRecommendedMode();

        Assert.Equal([(@"\\.\DISPLAY9", new DisplayModeSpec(1920, 1080, 60))], _modes.Applied);
        Assert.False(viewModel.CanApplyRecommendedMode);
        Assert.StartsWith("The virtual display now uses 1920 × 1080 at 60 Hz.", viewModel.ModeAdviceText, StringComparison.Ordinal);
        Assert.Contains("adding 1616 × 1080", viewModel.ModeAdviceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_changed_while_a_session_runs()
    {
        ConnectViewModel viewModel = Create(null);
        viewModel.SelectedMode = DisplayMode.Extend;
        viewModel.RefreshExtendStatus();
        Assert.True(viewModel.CanApplyRecommendedMode);

        _session.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Extend)));

        Assert.False(viewModel.CanApplyRecommendedMode);
        Assert.False(viewModel.CanBringWindowsBack);
        viewModel.ApplyRecommendedMode();
        viewModel.BringWindowsBack();
        Assert.Empty(_modes.Applied);
        Assert.Empty(_rescue.Calls);
    }

    [Fact]
    public void Bringing_windows_back_names_the_virtual_display_and_reports_the_count()
    {
        ConnectViewModel viewModel = Create(null);
        viewModel.SelectedMode = DisplayMode.Extend;
        viewModel.RefreshExtendStatus();

        viewModel.BringWindowsBack();

        Assert.Equal([@"\\.\DISPLAY9"], _rescue.Calls);
        Assert.StartsWith("Moved 2 windows to the main display.", viewModel.ModeAdviceText, StringComparison.Ordinal);
    }
}
