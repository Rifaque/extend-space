using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;

namespace ExtendSpace.Core.Tests.Display;

public sealed class VirtualDisplayDetectorTests
{
    private const uint Internal = 0x80000000;
    private const uint Hdmi = 5;

    internal static DisplayTarget Physical(string gdi, int left, string edid = "DEL", uint tech = Hdmi) =>
        new(gdi, "DELL U2723QE", edid, 0x4321, tech, true, new ScreenRect(left, 0, 2560, 1440));

    internal static DisplayTarget Vdd(string? gdi, bool active = true, int left = 2560, int width = 2400, int height = 1600) =>
        new(gdi, "VDD by MTT", "MTT", 0x1337, VirtualDisplayDetector.OutputIndirectVirtual, active, active ? new ScreenRect(left, 0, width, height) : null);

    [Fact]
    public void Without_any_virtual_display_Extend_explains_the_driver_and_how_to_get_it()
    {
        ExtendStatus status = VirtualDisplayDetector.Status([Physical(@"\\.\DISPLAY9", 0), Physical(@"\\.\DISPLAY1", -2560, "BOE", Internal)]);

        Assert.Equal(ExtendReadiness.NoVirtualDisplay, status.Readiness);
        Assert.Null(status.Display);
        Assert.Contains(VirtualDisplayDetector.InstallCommand, status.Message, StringComparison.Ordinal);
        Assert.Contains("does not install", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_VDD_monitor_that_is_not_in_the_desktop_asks_the_user_to_extend_onto_it()
    {
        ExtendStatus status = VirtualDisplayDetector.Status([Physical(@"\\.\DISPLAY9", 0), Vdd(null, active: false)]);

        Assert.Equal(ExtendReadiness.NotInDesktop, status.Readiness);
        Assert.Equal(VirtualDisplayProvider.VirtualDisplayDriver, status.Display!.Provider);
        Assert.Contains("Extend these displays", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_active_VDD_monitor_is_ready_with_its_name_and_size()
    {
        ExtendStatus status = VirtualDisplayDetector.Status([Physical(@"\\.\DISPLAY9", 0), Vdd(@"\\.\DISPLAY12")]);

        Assert.Equal(ExtendReadiness.Ready, status.Readiness);
        Assert.Equal(@"\\.\DISPLAY12", status.Display!.Target.GdiDeviceName);
        Assert.Contains("Display 12, 2400 × 1600", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_active_one_wins_over_an_inactive_one_and_VDD_over_an_unknown_provider()
    {
        DisplayTarget unknown = new(@"\\.\DISPLAY5", "Some Virtual", null, null, VirtualDisplayDetector.OutputIndirectVirtual, true, new ScreenRect(0, 0, 1920, 1080));
        ExtendStatus status = VirtualDisplayDetector.Status([Vdd(null, active: false), unknown, Vdd(@"\\.\DISPLAY14"), Vdd(@"\\.\DISPLAY13")]);

        Assert.Equal((@"\\.\DISPLAY13", VirtualDisplayProvider.VirtualDisplayDriver, 4), (status.Display!.Target.GdiDeviceName, status.Display.Provider, status.VirtualDisplayCount));
    }

    [Theory]
    [InlineData(VirtualDisplayDetector.OutputIndirectVirtual, "Generic", null, true)]
    [InlineData(VirtualDisplayDetector.OutputIndirectWired, "DisplayLink", null, false)]
    [InlineData(Hdmi, "VDD by MTT", null, true)]
    [InlineData(Hdmi, "Monitor", "MTT", true)]
    [InlineData(Internal, "Panel", "BOE", false)]
    public void Only_virtual_displays_are_virtual_and_a_USB_dock_monitor_is_not(uint tech, string name, string? edid, bool isVirtual)
    {
        var target = new DisplayTarget(@"\\.\DISPLAY3", name, edid, edid == "MTT" ? VirtualDisplayDetector.VddProductCode : 1, tech, true, null);
        Assert.Equal(isVirtual, VirtualDisplayDetector.Classify(target) is not null);
    }

    [Theory]
    [InlineData((ushort)0xAC10, "DEL")]
    [InlineData((ushort)0x9436, "MTT")]
    [InlineData((ushort)0x0000, null)]
    public void EDID_manufacturer_ids_are_decoded_from_their_swapped_bytes(ushort value, string? expected) =>
        Assert.Equal(expected, VirtualDisplayDetector.DecodeManufacturer(value));
}
