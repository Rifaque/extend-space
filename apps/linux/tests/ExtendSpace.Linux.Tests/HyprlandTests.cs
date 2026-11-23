using System.Buffers.Binary;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Linux.Hyprland;
using ExtendSpace.Linux.Wayland;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux.Tests;

public sealed class WaylandWireTests
{
    [Fact]
    public void A_request_has_the_object_size_and_opcode_header_then_padded_arguments()
    {
        // wl_registry.bind(name 7, "wl_seat", version 1, new id 9) on object 2.
        byte[] bytes = new WaylandWire.MessageBuilder(2, 0).Uint(7).String("wl_seat").Uint(1).Uint(9).Build();

        Assert.Equal(8 + 4 + 4 + 8 + 4 + 4, bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(((uint)bytes.Length << 16) | 0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12))); // "wl_seat" + NUL
        Assert.Equal("wl_seat\0\0"u8.ToArray()[..8], bytes[16..24]);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));
    }

    [Fact]
    public void Events_are_split_and_a_partial_one_waits_for_the_rest()
    {
        byte[] global = new WaylandWire.MessageBuilder(2, 0).Uint(31).String(VirtualPointer.ManagerInterface).Uint(2).Build();
        byte[] done = new WaylandWire.MessageBuilder(3, 0).Uint(5).Build();
        byte[] stream = [.. global, .. done[..6]];

        var events = new List<WaylandWire.Event>();
        int used = WaylandWire.Parse(stream, events);

        Assert.Equal(global.Length, used);
        WaylandWire.Event e = Assert.Single(events);
        Assert.Equal(2u, e.ObjectId);
        Assert.Equal(31u, e.Uint(0));
        Assert.Equal(VirtualPointer.ManagerInterface, e.String(4, out int next));
        Assert.Equal(2u, e.Uint(next));
    }

    [Fact]
    public void Fixed_point_is_24_8()
    {
        Assert.Equal(256, WaylandWire.Fixed(1));
        Assert.Equal(-15 * 256, WaylandWire.Fixed(-15));
    }

    [Fact]
    public void The_socket_is_found_from_the_session_variables()
    {
        Assert.Equal("/run/user/1000/wayland-1", WaylandConnection.SocketPath(n => n switch { "WAYLAND_DISPLAY" => "wayland-1", "XDG_RUNTIME_DIR" => "/run/user/1000", _ => null }));
        Assert.Equal("/tmp/w", WaylandConnection.SocketPath(n => n == "WAYLAND_DISPLAY" ? "/tmp/w" : null));
        Assert.Null(WaylandConnection.SocketPath(_ => null));
    }
}

public sealed class HyprlandTests
{
    private const string MonitorsJson = """
        [{"id":0,"name":"eDP-1","width":2560,"height":1600,"refreshRate":165.0,"x":3680,"y":80,"scale":1.6,"focused":false},
         {"id":1,"name":"HDMI-A-1","width":2560,"height":1440,"refreshRate":143.912,"x":5280,"y":20,"scale":1.25,"focused":true}]
        """;

    /// <summary>A fake hyprctl: records calls, keeps a monitor list that create/remove change.</summary>
    private sealed class FakeHyprctl(bool lua = true) : ICommandRunner
    {
        public List<string> Calls { get; } = [];

        public List<string> Extra { get; } = [];

        public (int, int) Mode { get; set; } = (1920, 1080);

        public (int ExitCode, string Output) Run(string program, params string[] arguments)
        {
            string call = string.Join(' ', arguments);
            Calls.Add(call);
            switch (arguments)
            {
                case ["-j", "monitors"]:
                    string extra = string.Concat(Extra.Select(name => $$""",{"id":9,"name":"{{name}}","width":{{Mode.Item1}},"height":{{Mode.Item2}},"refreshRate":60.0,"x":7328,"y":0,"scale":1,"focused":false}"""));
                    return (0, MonitorsJson.TrimEnd().TrimEnd(']') + extra + "]");
                case ["output", "create", "headless", var name]:
                    Extra.Add(name);
                    return (0, "ok");
                case ["output", "remove", var name]:
                    return Extra.Remove(name) ? (0, "ok") : (1, "no such output");
                case ["eval", _]:
                    if (!lua)
                    {
                        return (0, "unknown request");
                    }

                    Mode = (1616, 1080);
                    return (0, "ok");
                case ["keyword", "monitor", _]:
                    Mode = (1616, 1080);
                    return (0, "ok");
                default:
                    return (1, "unexpected");
            }
        }
    }

    [Fact]
    public void Monitors_are_read_from_hyprctl_json()
    {
        IReadOnlyList<HyprlandMonitor> monitors = Hyprctl.ParseMonitors(MonitorsJson)!;
        Assert.Equal(2, monitors.Count);
        Assert.Equal(new HyprlandMonitor(1, "HDMI-A-1", 2560, 1440, 143.912, 5280, 20, 1.25, true), monitors[1]);
        Assert.Null(Hyprctl.ParseMonitors("not json"));
    }

    [Fact]
    public void The_mirror_list_names_outputs_as_the_portal_does_and_leaves_out_the_extend_output()
    {
        var hyprctl = new FakeHyprctl();
        hyprctl.Extra.Add(HyprlandVirtualDisplay.OutputName);
        var displays = new X11Displays(new HyprlandMonitorSource(hyprctl));

        Assert.Equal(["eDP-1", "HDMI-A-1"], displays.List().Select(d => d.DeviceName));
        Assert.True(displays.List()[0].IsPrimary);
        Assert.Equal(2560, displays.List()[1].Width);
    }

    [Fact]
    public void Extend_creates_a_headless_output_sized_for_the_tablet_and_removes_it_after()
    {
        var hyprctl = new FakeHyprctl();
        var display = new HyprlandVirtualDisplay(hyprctl);

        HyprlandMonitor made = display.Create(1616, 1080);

        Assert.Equal((HyprlandVirtualDisplay.OutputName, 1616, 1080), (made.Name, made.Width, made.Height));
        Assert.Contains($"output create headless {HyprlandVirtualDisplay.OutputName}", hyprctl.Calls);
        Assert.Contains($"eval hl.monitor({{ output = \"{HyprlandVirtualDisplay.OutputName}\", mode = \"1616x1080@60\", position = \"auto-right\", scale = \"1\" }})", hyprctl.Calls);
        Assert.DoesNotContain(hyprctl.Calls, c => c.StartsWith("keyword", StringComparison.Ordinal));

        display.Remove();
        display.Remove();
        Assert.Empty(hyprctl.Extra);
        Assert.Single(hyprctl.Calls, c => c.StartsWith("output remove", StringComparison.Ordinal));
    }

    [Fact]
    public void Before_Lua_configuration_the_monitor_keyword_is_used()
    {
        var hyprctl = new FakeHyprctl(lua: false);
        HyprlandMonitor made = new HyprlandVirtualDisplay(hyprctl).Create(1616, 1080);
        Assert.Contains($"keyword monitor {HyprlandVirtualDisplay.OutputName},1616x1080@60,auto-right,1", hyprctl.Calls);
        Assert.Equal(1616, made.Width);
    }

    [Fact]
    public void An_output_left_by_a_crash_is_removed_and_physical_outputs_are_never_touched()
    {
        var hyprctl = new FakeHyprctl();
        hyprctl.Extra.Add("EXTENDSPACE-1");

        Assert.Equal(1, new HyprlandVirtualDisplay(hyprctl).RemoveLeftovers());
        Assert.Empty(hyprctl.Extra);
        Assert.DoesNotContain(hyprctl.Calls, c => c.Contains("eDP-1", StringComparison.Ordinal) || c.Contains("HDMI-A-1", StringComparison.Ordinal));
        Assert.True(HyprlandVirtualDisplay.IsOurs("EXTENDSPACE-1"));
        Assert.False(HyprlandVirtualDisplay.IsOurs("HEADLESS-1"));
    }

    [Fact]
    public void Hyprland_is_recognised_by_its_instance_signature_or_desktop_name()
    {
        Assert.True(LinuxEnvironment.Detect(n => n switch { "WAYLAND_DISPLAY" => "wayland-1", "HYPRLAND_INSTANCE_SIGNATURE" => "abc", _ => null }).IsHyprland);
        Assert.True(new LinuxEnvironment(DisplayServer.Wayland, "Hyprland", null).IsHyprland);
        Assert.False(new LinuxEnvironment(DisplayServer.Wayland, "GNOME", null).IsHyprland);
        Assert.False(new LinuxEnvironment(DisplayServer.X11, "Hyprland", ":0").IsHyprland);
    }

    [Fact]
    public void Hyprland_Extend_is_ready_and_explains_the_one_time_dialog()
    {
        ExtendStatus status = new LinuxExtendStatus(new LinuxEnvironment(DisplayServer.Wayland, "Hyprland", null), () => 7, () => (1616, 1080)).Status();
        Assert.Equal(ExtendReadiness.Ready, status.Readiness);
        Assert.Contains("1616 × 1080", status.Message, StringComparison.Ordinal);
        Assert.Contains(HyprlandVirtualDisplay.OutputName, status.Message, StringComparison.Ordinal);
    }
}

public sealed class InputAvailabilityTests
{
    [Fact]
    public void On_Hyprland_touch_is_refused_with_the_reason_and_mouse_uses_the_virtual_pointer()
    {
        var availability = new LinuxInputAvailability(new LinuxEnvironment(DisplayServer.Wayland, "Hyprland", null), new WaylandInputSupport(RemoteDesktopPortal: false, VirtualPointer: true));
        Assert.Contains("Not available on Hyprland", availability.Unavailable(InputMode.Touch), StringComparison.Ordinal);
        Assert.Null(availability.Unavailable(InputMode.Mouse));
        Assert.Null(availability.Unavailable(InputMode.Off));
        Assert.Contains("virtual-pointer", availability.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_remote_desktop_portal_touch_and_mouse_are_offered()
    {
        var availability = new LinuxInputAvailability(new LinuxEnvironment(DisplayServer.Wayland, "KDE", null), new WaylandInputSupport(true, false));
        Assert.Null(availability.Unavailable(InputMode.Touch));
        Assert.Null(availability.Unavailable(InputMode.Mouse));
    }

    [Fact]
    public void A_desktop_with_neither_path_offers_no_input_and_X11_offers_mouse_only()
    {
        var none = new LinuxInputAvailability(new LinuxEnvironment(DisplayServer.Wayland, "Unknown", null), new WaylandInputSupport(false, false));
        Assert.NotNull(none.Unavailable(InputMode.Mouse));
        var x11 = new LinuxInputAvailability(new LinuxEnvironment(DisplayServer.X11, "XFCE", ":0"), new WaylandInputSupport(false, false));
        Assert.Null(x11.Unavailable(InputMode.Mouse));
        Assert.NotNull(x11.Unavailable(InputMode.Touch));
    }
}

public sealed class HostStateFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"es-test-{Guid.NewGuid():N}");

    public HostStateFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Restore_tokens_are_kept_per_output_across_restarts()
    {
        string path = Path.Combine(_folder, "portal-tokens.json");
        new PortalTokenStore(path).Set("EXTENDSPACE-1", "a");
        var again = new PortalTokenStore(path);
        again.Set("eDP-1", "b");

        var third = new PortalTokenStore(path);
        Assert.Equal("a", third.Get("EXTENDSPACE-1"));
        Assert.Equal("b", third.Get("eDP-1"));
        Assert.Null(third.Get("HDMI-A-1"));
        third.Set("eDP-1", null);
        Assert.Null(new PortalTokenStore(path).Get("eDP-1"));
    }

    [Fact]
    public void A_damaged_token_file_only_means_asking_again()
    {
        string path = Path.Combine(_folder, "portal-tokens.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(new PortalTokenStore(path).Get("eDP-1"));
    }

    [Fact]
    public void The_last_tablet_screen_is_read_back()
    {
        string path = Path.Combine(_folder, "last-tablet.json");
        File.WriteAllText(path, """{"width":3200,"height":2136,"refreshHz":144}""");
        Assert.Equal(new TabletScreen(3200, 2136, 144), LinuxHost.LoadTablet(path));
        File.WriteAllText(path, "[]");
        Assert.Null(LinuxHost.LoadTablet(path));
        Assert.Null(LinuxHost.LoadTablet(Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void Only_one_instance_holds_the_lock_and_it_is_free_again_after()
    {
        using (FileStream? first = InstanceLock.TryAcquire(_folder))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(_folder));
        }

        using FileStream? again = InstanceLock.TryAcquire(_folder);
        Assert.NotNull(again);
    }
}
