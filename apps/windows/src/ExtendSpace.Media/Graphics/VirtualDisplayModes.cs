using System.Runtime.InteropServices;
using ExtendSpace.Core.Display;

namespace ExtendSpace.Media.Graphics;

/// <summary>
/// The virtual display's modes on Windows (D92): listed with <c>EnumDisplaySettings</c>; changed with
/// <c>ChangeDisplaySettingsEx</c> on that one device only. A device that the detector does not
/// classify as a virtual display is refused before Windows is asked anything, so the physical
/// monitors' modes cannot be changed through this class.
/// </summary>
public sealed class VirtualDisplayModes(IVirtualDisplayCatalog catalog) : IVirtualDisplayModes
{
    private const int EnumCurrentSettings = -1;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;
    private const int CdsUpdateRegistry = 0x00000001;
    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;

    public IReadOnlyList<DisplayModeSpec> Available(string deviceName)
    {
        var modes = new List<DisplayModeSpec>();
        var mode = NewMode();
        for (int index = 0; EnumDisplaySettingsW(deviceName, index, ref mode); index++)
        {
            var spec = new DisplayModeSpec(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
            if (!modes.Contains(spec))
            {
                modes.Add(spec);
            }
        }

        return modes;
    }

    public DisplayModeSpec? Current(string deviceName)
    {
        var mode = NewMode();
        return EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref mode)
            ? new DisplayModeSpec(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency)
            : null;
    }

    public string? Apply(string deviceName, DisplayModeSpec mode)
    {
        if (Guard(catalog.Targets(), deviceName) is { } refused)
        {
            return refused;
        }

        if (!Available(deviceName).Contains(mode))
        {
            return $"{deviceName} does not offer {mode}.";
        }

        var devmode = NewMode();
        devmode.PelsWidth = mode.Width;
        devmode.PelsHeight = mode.Height;
        devmode.DisplayFrequency = mode.RefreshHz;
        devmode.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int result = ChangeDisplaySettingsExW(deviceName, ref devmode, 0, CdsUpdateRegistry, 0);
        return result switch
        {
            DispChangeSuccessful => null,
            DispChangeRestart => "Windows will use the new mode after a restart.",
            _ => $"Windows refused the mode (code {result}).",
        };
    }

    /// <summary>Null when <paramref name="deviceName"/> is an active virtual display; otherwise why not.</summary>
    public static string? Guard(IReadOnlyList<DisplayTarget> targets, string deviceName)
    {
        DisplayTarget? target = targets.FirstOrDefault(t => string.Equals(t.GdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return $"{deviceName} is not a display Windows lists.";
        }

        return VirtualDisplayDetector.Classify(target) is null
            ? $"{deviceName} is not a virtual display; ExtendSpace changes only the virtual display's mode."
            : null;
    }

    private static DevMode NewMode() => new() { Size = (short)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNumber, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DevMode mode, nint window, int flags, nint parameter);
}
