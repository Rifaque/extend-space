using ExtendSpace.Media.Graphics;

namespace ExtendSpace.Media.Tests;

/// <summary>Which monitor a session captures (D68). No monitor or GPU is involved.</summary>
public sealed class MonitorSelectionTests
{
    private static readonly DisplayMonitor Left = new(0, 1, @"\\.\DISPLAY2", false, -1920, 0, 1920, 1080);
    private static readonly DisplayMonitor Main = new(1, 2, @"\\.\DISPLAY1", true, 0, 0, 2560, 1440);

    [Fact]
    public void With_nothing_chosen_the_main_display_is_captured()
    {
        Assert.Same(Main, DesktopCaptureSource.SelectMonitor([Left, Main], null));
    }

    [Fact]
    public void A_display_chosen_by_name_is_captured_whatever_its_position_and_case()
    {
        Assert.Same(Left, DesktopCaptureSource.SelectMonitor([Main, Left], null, @"\\.\display2"));
        Assert.Same(Left, DesktopCaptureSource.SelectMonitor([Left, Main], requestedIndex: 1, requestedDeviceName: @"\\.\DISPLAY2"));
    }

    [Fact]
    public void A_chosen_display_that_has_gone_fails_and_is_never_replaced_by_another()
    {
        MediaSourceException failure = Assert.Throws<MediaSourceException>(() => DesktopCaptureSource.SelectMonitor([Main], null, @"\\.\DISPLAY2"));

        Assert.Equal(MediaFaultCodes.MonitorNotFound, failure.Code);
        Assert.Contains("Display 2", failure.Message);
        Assert.Contains("no longer connected", failure.Message);
    }

    [Fact]
    public void Displays_are_labelled_as_Windows_Settings_numbers_them()
    {
        Assert.Equal("Display 2", DisplayCatalog.Label(@"\\.\DISPLAY2"));
        Assert.Equal("Display 12", DisplayCatalog.Label(@"\\.\DISPLAY12"));
        Assert.Equal(@"\\.\WEIRD", DisplayCatalog.Label(@"\\.\WEIRD"));
        Assert.Equal(@"\\.\DISPLAYx", DisplayCatalog.Label(@"\\.\DISPLAYx"));
    }
}
