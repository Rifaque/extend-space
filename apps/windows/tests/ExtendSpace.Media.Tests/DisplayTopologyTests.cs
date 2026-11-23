using ExtendSpace.Core.Display;
using ExtendSpace.Media.Graphics;
using ExtendSpace.Media.Input;

namespace ExtendSpace.Media.Tests;

/// <summary>The real display configuration of the PC running the tests, read only (D88).</summary>
public sealed class DisplayTopologyTests
{
    [Fact]
    public void Every_active_target_has_a_GDI_name_and_matches_a_monitor_Windows_enumerates()
    {
        IReadOnlyList<DisplayTarget> targets = new DisplayTopology().Targets();
        foreach (DisplayTarget target in targets)
        {
            Console.WriteLine($"TARGET gdi={target.GdiDeviceName} name='{target.FriendlyName}' edid={target.EdidManufacturer}/{target.EdidProductCode:X4} tech=0x{target.OutputTechnology:X} active={target.IsActive} bounds={target.Bounds}");
        }

        List<DisplayTarget> active = [.. targets.Where(target => target.IsActive)];
        Assert.NotEmpty(active);
        // In physical pixels, as the topology is: this test process is not DPI aware.
        IReadOnlyList<DisplayMonitor> monitors;
        using (ExtendSpace.Media.Input.PhysicalPixels.Enter())
        {
            monitors = DisplayMonitors.Enumerate();
        }

        Assert.Equal(monitors.Count, active.Count);
        Assert.All(active, target =>
        {
            Assert.NotNull(target.GdiDeviceName);
            Assert.NotNull(target.Bounds);
            Assert.Contains(monitors, monitor => monitor.DeviceName == target.GdiDeviceName && monitor.Width == target.Bounds!.Value.Width);
        });
    }

    [Fact]
    public void Reading_the_topology_twice_gives_the_same_answer_and_changes_nothing()
    {
        var topology = new DisplayTopology();
        IReadOnlyList<DisplayMonitor> before = DisplayMonitors.Enumerate();
        IReadOnlyList<DisplayTarget> first = topology.Targets();
        IReadOnlyList<DisplayTarget> second = topology.Targets();
        IReadOnlyList<DisplayMonitor> after = DisplayMonitors.Enumerate();

        Assert.Equal(first, second);
        Assert.Equal(before.Select(m => (m.DeviceName, m.Left, m.Top, m.Width, m.Height)), after.Select(m => (m.DeviceName, m.Left, m.Top, m.Width, m.Height)));
        Console.WriteLine($"EXTEND STATUS: {VirtualDisplayDetector.Status(first).Readiness}");
    }
}
