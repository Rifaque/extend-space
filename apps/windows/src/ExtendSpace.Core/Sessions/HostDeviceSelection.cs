namespace ExtendSpace.Core.Sessions;

/// <summary>
/// What the user chose on the Connect screen, for the components that act on it when a session
/// starts (D65, D75): the route, the device on it, and the display mirrored. The view model sets it
/// just before it starts a session. Thread-safe.
/// </summary>
public sealed class HostDeviceSelection
{
    private volatile string? _serial;
    private volatile string? _wirelessDeviceId;
    private volatile string? _monitorDeviceName;
    private volatile int _transport = (int)TransportKind.Usb;
    private volatile int _mode = (int)DisplayMode.Mirror;

    /// <summary>Mirror, or Extend onto the virtual monitor named by <see cref="MonitorDeviceName"/> (D89).</summary>
    public DisplayMode Mode
    {
        get => (DisplayMode)_mode;
        set => _mode = (int)value;
    }

    /// <summary>
    /// The Android device as adb names it, for USB. Null means "the only device attached" (or
    /// <c>ANDROID_SERIAL</c>, if set).
    /// </summary>
    public string? Serial
    {
        get => _serial;
        set => _serial = value;
    }

    /// <summary>The route the next session uses.</summary>
    public TransportKind Transport
    {
        get => (TransportKind)_transport;
        set => _transport = (int)value;
    }

    /// <summary>The paired tablet's device ID, for Wi-Fi.</summary>
    public string? WirelessDeviceId
    {
        get => _wirelessDeviceId;
        set => _wirelessDeviceId = value;
    }

    /// <summary>The mirrored monitor's device name; null for the main display. Input maps touches onto it (D83).</summary>
    public string? MonitorDeviceName
    {
        get => _monitorDeviceName;
        set => _monitorDeviceName = value;
    }
}
