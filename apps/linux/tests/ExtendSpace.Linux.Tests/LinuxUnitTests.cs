using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Linux.Gstreamer;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux.Tests;

public sealed class PipelineDescriptionTests
{
    [Fact]
    public void An_X11_region_is_captured_scaled_and_encoded_with_x264_into_Annex_B_access_units()
    {
        string text = PipelineDescription.Build(new CaptureSource.X11Region(":0", new ScreenRect(1920, 0, 1616, 1080)), 1616, 1080, 60, 12_000_000, PipelineDescription.Encoders[^1]);

        Assert.StartsWith("ximagesrc display-name=:0 startx=1920 starty=0 endx=3535 endy=1079 use-damage=false show-pointer=true ! videorate ! video/x-raw,framerate=60/1", text, StringComparison.Ordinal);
        Assert.Contains("format=I420,width=1616,height=1080", text, StringComparison.Ordinal);
        Assert.Contains("x264enc name=es_encoder tune=zerolatency speed-preset=ultrafast bitrate=12000 key-int-max=120 bframes=0", text, StringComparison.Ordinal);
        Assert.EndsWith("h264parse config-interval=-1 ! video/x-h264,stream-format=byte-stream,alignment=au,profile=constrained-baseline ! appsink name=es_sink sync=false max-buffers=4 drop=false emit-signals=false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_portal_virtual_stream_offers_its_size_first_and_repeats_still_pictures()
    {
        string text = PipelineDescription.Build(new CaptureSource.PipeWireNode(42, 77, 1616, 1080), 1616, 1080, 60, 12_000_000, PipelineDescription.Encoders[0]);

        Assert.StartsWith("pipewiresrc fd=42 path=77 do-timestamp=true keepalive-time=500 always-copy=true ! video/x-raw,width=1616,height=1080 ! ", text, StringComparison.Ordinal);
        Assert.Contains("format=NV12", text, StringComparison.Ordinal);
        Assert.Contains("vah264enc name=es_encoder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("profile=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_h264parse_the_encoders_own_byte_stream_goes_to_the_sink()
    {
        string text = PipelineDescription.Build(new CaptureSource.X11Region(":0", new ScreenRect(0, 0, 640, 360)), 640, 360, 30, 2_000_000, PipelineDescription.Encoders[^1], parser: false);
        Assert.DoesNotContain("h264parse", text, StringComparison.Ordinal);
        Assert.Contains("byte-stream=true ! video/x-h264,stream-format=byte-stream,alignment=au", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hardware_encoders_are_preferred_in_order_and_x264_is_the_fallback()
    {
        Assert.Equal("nvh264enc", PipelineDescription.ChooseEncoder(e => e is "nvh264enc" or "x264enc")!.Element);
        Assert.Equal("x264enc", PipelineDescription.ChooseEncoder(e => e is "nvh264enc" or "x264enc", allowHardware: false)!.Element);
        Assert.Null(PipelineDescription.ChooseEncoder(_ => false));
    }

    [Fact]
    public void An_IDR_is_found_after_SPS_and_PPS_with_either_start_code()
    {
        byte[] idr = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9, 9];
        byte[] p = [0, 0, 0, 1, 0x09, 0x10, 0, 0, 1, 0x41, 9, 9];
        Assert.True(AnnexB.IsKeyframe(idr));
        Assert.False(AnnexB.IsKeyframe(p));
        Assert.False(AnnexB.IsKeyframe([]));
    }
}

public sealed class X11VirtualDisplayTests
{
    private sealed class FakeXrandr(string screen, params string[] failOn) : ICommandRunner
    {
        public List<string> Calls { get; } = [];

        public (int ExitCode, string Output) Run(string program, params string[] arguments)
        {
            string call = string.Join(' ', arguments.Skip(2));
            Calls.Add(call);
            if (call.Length == 0)
            {
                return (0, screen);
            }

            return failOn.Any(call.StartsWith) ? (1, "BadMatch") : (0, string.Empty);
        }
    }

    private const string Laptop = "Screen 0: minimum 8 x 8, current 2560 x 1600, maximum 16384 x 16384\neDP-1 connected primary 2560x1600+0+0";

    [Fact]
    public void The_screen_line_is_read()
    {
        Assert.Equal(new X11Screen(2560, 1600, 16384, 16384), X11VirtualDisplay.ParseScreen(Laptop));
        Assert.Null(X11VirtualDisplay.ParseScreen("garbage"));
    }

    [Fact]
    public void The_virtual_monitor_goes_right_of_everything_and_the_framebuffer_is_restored_after()
    {
        var xrandr = new FakeXrandr(Laptop);
        var display = new X11VirtualDisplay(xrandr, ":0");

        ScreenRect region = display.Create(1616, 1080);
        display.Remove();
        display.Remove();

        Assert.Equal(new ScreenRect(2560, 0, 1616, 1080), region);
        Assert.Equal(["", "--fb 4176x1600", "--setmonitor EXTENDSPACE-VIRTUAL 1616/428x1080/286+2560+0 none", "--delmonitor EXTENDSPACE-VIRTUAL", "--fb 2560x1600"], xrandr.Calls);
    }

    [Fact]
    public void A_failed_monitor_declaration_restores_the_framebuffer_at_once()
    {
        var xrandr = new FakeXrandr(Laptop, "--setmonitor");
        var display = new X11VirtualDisplay(xrandr, ":0");

        Assert.Throws<InvalidOperationException>(() => display.Create(1616, 1080));
        Assert.Equal("--fb 2560x1600", xrandr.Calls[^1]);
    }

    [Fact]
    public void A_screen_that_cannot_grow_refuses_with_its_limit()
    {
        Assert.Null(X11VirtualDisplay.Plan(new X11Screen(3840, 2160, 4096, 4096), 1616, 1080, out string? problem));
        Assert.Contains("at most 4096 × 4096", problem, StringComparison.Ordinal);
    }
}

public sealed class LinuxEnvironmentTests
{
    private static LinuxEnvironment Detect(params (string Key, string Value)[] variables) =>
        LinuxEnvironment.Detect(name => variables.FirstOrDefault(v => v.Key == name).Value);

    [Fact]
    public void Wayland_wins_over_XWaylands_DISPLAY()
    {
        LinuxEnvironment e = Detect(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"));
        Assert.Equal(DisplayServer.Wayland, e.Server);
        Assert.True(e.IsGnomeOrKde);
    }

    [Fact]
    public void An_X11_session_and_no_session_are_told_apart()
    {
        Assert.Equal(DisplayServer.X11, Detect(("DISPLAY", ":0"), ("XDG_SESSION_TYPE", "x11"), ("XDG_CURRENT_DESKTOP", "X-Cinnamon")).Server);
        Assert.Equal(DisplayServer.None, Detect().Server);
    }

    [Theory]
    [InlineData("GNOME", 5u, ExtendReadiness.Ready, "asks to share a new virtual display (1616 × 1080)")]
    [InlineData("KDE", 1u, ExtendReadiness.NoVirtualDisplay, "cannot create virtual displays")]
    [InlineData("GNOME", null, ExtendReadiness.NoVirtualDisplay, "portal is not available")]
    [InlineData("sway", 5u, ExtendReadiness.NoVirtualDisplay, "swaymsg create_output")]
    public void Wayland_Extend_readiness_follows_the_portal_and_desktop(string desktop, uint? sourceTypes, ExtendReadiness readiness, string message)
    {
        var status = new LinuxExtendStatus(new LinuxEnvironment(DisplayServer.Wayland, desktop, null), () => sourceTypes, () => (1616, 1080)).Status();
        Assert.Equal(readiness, status.Readiness);
        Assert.Contains(message, status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void X11_Extend_is_offered_as_experimental()
    {
        var status = new LinuxExtendStatus(new LinuxEnvironment(DisplayServer.X11, "XFCE", ":0"), () => null, () => (1920, 1080)).Status();
        Assert.Equal(ExtendReadiness.Ready, status.Readiness);
        Assert.Equal(LinuxSourceNames.Virtual, status.Display!.Target.GdiDeviceName);
        Assert.Contains("Experimental on X11", status.Message, StringComparison.Ordinal);
    }
}
