using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Media.Input;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace ExtendSpace.Media.Graphics;

/// <summary>
/// Windows' display configuration, read only (D88): every display target that is available, whether it
/// is part of the desktop, and what it says about itself. Never calls <c>SetDisplayConfig</c>.
/// </summary>
public sealed unsafe class DisplayTopology : IVirtualDisplayCatalog
{
    private const uint DisplayConfigPathActive = 0x1;

    // Declared here: the generated overload requires a topology id, which must be null for QDC_ALL_PATHS.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, DISPLAYCONFIG_PATH_INFO* paths, ref uint modeCount, DISPLAYCONFIG_MODE_INFO* modes, nint topologyId);

    public IReadOnlyList<DisplayTarget> Targets()
    {
        using PhysicalPixels physical = PhysicalPixels.Enter();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, out uint pathCount, out uint modeCount) != WIN32_ERROR.ERROR_SUCCESS)
            {
                return [];
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR result;
            fixed (DISPLAYCONFIG_PATH_INFO* pathPointer = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modePointer = modes)
            {
                result = (WIN32_ERROR)QueryDisplayConfig((uint)QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, ref pathCount, pathPointer, ref modeCount, modePointer, 0);
            }

            if (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                // The topology changed between the two calls; ask again.
                continue;
            }

            return result == WIN32_ERROR.ERROR_SUCCESS ? Read(paths.AsSpan(0, (int)pathCount)) : [];
        }

        return [];
    }

    private static List<DisplayTarget> Read(ReadOnlySpan<DISPLAYCONFIG_PATH_INFO> paths)
    {
        IReadOnlyList<DisplayMonitor> monitors;
        try
        {
            monitors = DisplayMonitors.Enumerate();
        }
        catch (InvalidOperationException)
        {
            monitors = [];
        }

        // QDC_ALL_PATHS lists every source-target pair: one entry per target, active if any path to it is.
        var byTarget = new Dictionary<(long Adapter, uint Target), DisplayTarget>();
        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            bool active = (path.flags & DisplayConfigPathActive) != 0;
            if (!active && !path.targetInfo.targetAvailable)
            {
                continue;
            }

            var key = (((long)path.targetInfo.adapterId.HighPart << 32) | path.targetInfo.adapterId.LowPart, path.targetInfo.id);
            if (byTarget.TryGetValue(key, out DisplayTarget? known) && (known.IsActive || !active))
            {
                continue;
            }

            string? gdi = active ? SourceName(path) : null;
            ScreenRect? bounds = gdi is null
                ? null
                : monitors.FirstOrDefault(monitor => string.Equals(monitor.DeviceName, gdi, StringComparison.OrdinalIgnoreCase)) is { } monitor
                    ? new ScreenRect(monitor.Left, monitor.Top, monitor.Width, monitor.Height)
                    : null;
            byTarget[key] = TargetName(path, gdi, active, bounds);
        }

        return [.. byTarget.Values];
    }

    private static string? SourceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var source = default(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        return PInvoke.DisplayConfigGetDeviceInfo(&source.header) == (int)WIN32_ERROR.ERROR_SUCCESS ? source.viewGdiDeviceName.ToString() : null;
    }

    private static DisplayTarget TargetName(DISPLAYCONFIG_PATH_INFO path, string? gdi, bool active, ScreenRect? bounds)
    {
        var target = default(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        if (PInvoke.DisplayConfigGetDeviceInfo(&target.header) != (int)WIN32_ERROR.ERROR_SUCCESS)
        {
            return new DisplayTarget(gdi, string.Empty, null, null, (uint)path.targetInfo.outputTechnology, active, bounds);
        }

        bool edidValid = (target.flags.Anonymous.value & 0x4) != 0; // edidIdsValid
        return new DisplayTarget(
            gdi,
            target.monitorFriendlyDeviceName.ToString(),
            edidValid ? VirtualDisplayDetector.DecodeManufacturer(target.edidManufactureId) : null,
            edidValid ? target.edidProductCodeId : null,
            (uint)target.outputTechnology,
            active,
            bounds,
            target.monitorDevicePath.ToString());
    }
}
