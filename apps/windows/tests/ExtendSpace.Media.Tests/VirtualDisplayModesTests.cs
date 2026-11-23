using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Media.Graphics;

namespace ExtendSpace.Media.Tests;

public sealed class VirtualDisplayModesTests
{
    // As read on the development PC (2026-11-23): VDD reports HDMI, so only its EDID marks it virtual (ES-142).
    private static readonly DisplayTarget Acer = new(@"\.\DISPLAY5", "VG271U M3", "ACR", 0x0BDD, 5, true, new ScreenRect(0, 0, 2560, 1440));
    private static readonly DisplayTarget Panel = new(@"\.\DISPLAY1", "", "SDC", 0x420A, 0x80000000, true, new ScreenRect(-2560, -72, 2560, 1600));
    private static readonly DisplayTarget Vdd = new(@"\.\DISPLAY9", "VDD by MTT", "MTT", 0x1337, 5, true, new ScreenRect(2560, 0, 800, 600));

    [Theory]
    [InlineData(@"\.\DISPLAY5")]
    [InlineData(@"\.\DISPLAY1")]
    [InlineData(@"\.\DISPLAY7")]
    public void Physical_or_unknown_displays_are_refused_before_Windows_is_asked(string device)
    {
        Assert.NotNull(VirtualDisplayModes.Guard([Acer, Panel, Vdd], device));
    }

    [Fact]
    public void The_virtual_display_is_accepted_whatever_the_case_of_its_name()
    {
        Assert.Null(VirtualDisplayModes.Guard([Acer, Panel, Vdd], @"\.\display9"));
    }

    [Fact]
    public void Applying_to_a_physical_monitor_through_the_real_class_is_refused()
    {
        var modes = new VirtualDisplayModes(new Fixed([Acer, Panel, Vdd]));
        Assert.Contains("not a virtual display", modes.Apply(@"\.\DISPLAY5", new DisplayModeSpec(2560, 1440, 60)), StringComparison.Ordinal);
    }

    [Fact]
    public void This_PCs_virtual_display_modes_can_be_read()
    {
        DisplayTarget? vdd = new DisplayTopology().Targets().FirstOrDefault(t => VirtualDisplayDetector.Classify(t) is not null && t.IsActive);
        Assert.SkipWhen(vdd?.GdiDeviceName is null, "no active virtual display on this PC");
        var modes = new VirtualDisplayModes(new DisplayTopology());
        IReadOnlyList<DisplayModeSpec> available = modes.Available(vdd!.GdiDeviceName!);
        TestContext.Current.SendDiagnosticMessage($"MODES {vdd.GdiDeviceName}: current {modes.Current(vdd.GdiDeviceName!)}; {available.Count} modes");
        Assert.NotEmpty(available);
        Assert.Contains(modes.Current(vdd.GdiDeviceName!)!, available);
    }

    private sealed class Fixed(IReadOnlyList<DisplayTarget> targets) : IVirtualDisplayCatalog
    {
        public IReadOnlyList<DisplayTarget> Targets() => targets;
    }
}
