using ExtendSpace.Core.Input;

namespace ExtendSpace.Core.Display;

/// <summary>
/// One display target as Windows' display configuration reports it (D88): read with
/// <c>QueryDisplayConfig</c> and <c>DisplayConfigGetDeviceInfo</c>, never written.
/// </summary>
/// <param name="GdiDeviceName">The source's GDI name (<c>\\.\DISPLAY3</c>) while the target is part of the desktop; null otherwise.</param>
/// <param name="EdidManufacturer">The three-letter PnP id from the EDID, when Windows has it.</param>
/// <param name="OutputTechnology">A <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> value.</param>
/// <param name="Bounds">Position and size on the desktop in physical pixels, while active.</param>
public sealed record DisplayTarget(
    string? GdiDeviceName,
    string FriendlyName,
    string? EdidManufacturer,
    int? EdidProductCode,
    uint OutputTechnology,
    bool IsActive,
    ScreenRect? Bounds,
    string? MonitorDevicePath = null);

public enum VirtualDisplayProvider
{
    /// <summary>VirtualDrivers/Virtual-Display-Driver ("VDD", MttVDD), D87.</summary>
    VirtualDisplayDriver,

    /// <summary>Some other indirect virtual display (Windows reports INDIRECT_VIRTUAL).</summary>
    Unknown,
}

public sealed record VirtualDisplay(DisplayTarget Target, VirtualDisplayProvider Provider);

public enum ExtendReadiness
{
    /// <summary>No virtual display exists: the driver is not installed, or it is disabled.</summary>
    NoVirtualDisplay,

    /// <summary>A virtual display exists but is not part of the desktop (disconnected in Display settings).</summary>
    NotInDesktop,

    /// <summary>A virtual display is part of the desktop: Extend can capture it.</summary>
    Ready,
}

public sealed record ExtendStatus(ExtendReadiness Readiness, VirtualDisplay? Display, string Message, int VirtualDisplayCount = 0);

/// <summary>What the Connect screen asks about Extend. Implemented over the display configuration (ExtendSpace.Media).</summary>
/// <summary>
/// A catalog that knows Extend's readiness itself (D98): on Linux the virtual display is created when
/// the session starts (portal or RandR), so there is nothing to detect beforehand.
/// </summary>
public interface IExtendStatusSource
{
    ExtendStatus Status();
}

public interface IVirtualDisplayCatalog
{
    IReadOnlyList<DisplayTarget> Targets();
}

/// <summary>Which display targets are virtual displays, and whether Extend can use one (D88). Pure.</summary>
public static class VirtualDisplayDetector
{
    public const uint OutputIndirectWired = 16;
    public const uint OutputIndirectVirtual = 17;

    /// <summary>VDD's EDID (D87): manufacturer "MTT", product 0x1337.</summary>
    public const string VddManufacturer = "MTT";
    public const int VddProductCode = 0x1337;

    public const string InstallCommand = "winget install --id=VirtualDrivers.Virtual-Display-Driver -e";

    public static VirtualDisplayProvider? Classify(DisplayTarget target)
    {
        if (target.EdidManufacturer == VddManufacturer && target.EdidProductCode == VddProductCode)
        {
            return VirtualDisplayProvider.VirtualDisplayDriver;
        }

        if (target.FriendlyName.Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDisplayProvider.VirtualDisplayDriver;
        }

        // A wired indirect display (a USB dock driving a real monitor) is not virtual.
        return target.OutputTechnology == OutputIndirectVirtual ? VirtualDisplayProvider.Unknown : null;
    }

    /// <summary>
    /// The Extend status for these targets: a virtual display that is part of the desktop wins; VDD before
    /// unknown providers; then the one with the lowest GDI number, so that the choice is stable.
    /// </summary>
    public static ExtendStatus Status(IReadOnlyList<DisplayTarget> targets)
    {
        List<VirtualDisplay> virtuals = [.. targets
            .Select(target => Classify(target) is { } provider ? new VirtualDisplay(target, provider) : null)
            .OfType<VirtualDisplay>()
            .OrderByDescending(display => display.Target.IsActive)
            .ThenBy(display => display.Provider)
            .ThenBy(display => display.Target.GdiDeviceName ?? "~", StringComparer.OrdinalIgnoreCase)];

        if (virtuals.Count == 0)
        {
            return new ExtendStatus(
                ExtendReadiness.NoVirtualDisplay,
                null,
                $"Extend needs the free Virtual Display Driver, which ExtendSpace does not install for you. Install it (for example: {InstallCommand}), then come back.");
        }

        VirtualDisplay chosen = virtuals[0];
        if (!chosen.Target.IsActive || chosen.Target.GdiDeviceName is null)
        {
            return new ExtendStatus(
                ExtendReadiness.NotInDesktop,
                chosen,
                "The virtual display is installed but not part of your desktop. Open Windows Settings > System > Display, select it, and choose Extend these displays.",
                virtuals.Count);
        }

        string size = chosen.Target.Bounds is { } bounds ? $"{bounds.Width} × {bounds.Height}" : "unknown size";
        return new ExtendStatus(
            ExtendReadiness.Ready,
            chosen,
            $"The tablet will be {Sessions.DisplayInfo.LabelFor(chosen.Target.GdiDeviceName)}, {size}. Move windows onto it, and arrange or resize it in Display settings.",
            virtuals.Count);
    }

    /// <summary>The three-letter PnP id of a DISPLAYCONFIG <c>edidManufactureId</c>, which holds the EDID's two bytes swapped.</summary>
    public static string? DecodeManufacturer(ushort edidManufactureId)
    {
        int id = ((edidManufactureId & 0xFF) << 8) | (edidManufactureId >> 8);
        char[] letters = [(char)('@' + ((id >> 10) & 0x1F)), (char)('@' + ((id >> 5) & 0x1F)), (char)('@' + (id & 0x1F))];
        return letters.All(letter => letter is >= 'A' and <= 'Z') ? new string(letters) : null;
    }
}
