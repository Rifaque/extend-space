using ExtendSpace.Core.Sessions;
using ExtendSpace.Media.Graphics;

namespace ExtendSpace.Media;

/// <summary>
/// The monitors attached now, for the Connect screen's display choice (D68). Sizes are in pixels in a
/// per-monitor-DPI-aware process, which the host is (its manifest says PerMonitorV2).
/// </summary>
public sealed class DisplayCatalog : IDisplayCatalog
{
    public IReadOnlyList<DisplayInfo> List() =>
        [.. DisplayMonitors.Enumerate().Select(monitor => new DisplayInfo(
            monitor.DeviceName,
            monitor.IsPrimary,
            monitor.Width,
            monitor.Height,
            DisplayMonitors.RefreshRate(monitor)))];

    /// <summary>"Display 2" for <c>\\.\DISPLAY2</c>; see <see cref="DisplayInfo.LabelFor"/>.</summary>
    public static string Label(string deviceName) => DisplayInfo.LabelFor(deviceName);
}
