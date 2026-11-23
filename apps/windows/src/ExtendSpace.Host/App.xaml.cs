using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Media;
using ExtendSpace.Media.Graphics;
using ExtendSpace.Media.Input;
using ExtendSpace.Media.Wireless;
using Microsoft.UI.Xaml;

namespace ExtendSpace.Host;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The device chosen on the Connect screen, read by the USB transport when a session starts (D65).</summary>
    private static readonly HostDeviceSelection DeviceSelection = new();

    /// <summary>
    /// The one session this host can hold, and its only owner. Lives for the whole process. Its
    /// connection is the real USB one and its pipeline the real capture and H.264 encoder (stage 4).
    /// Connect is enabled for USB Mirror (D65); the device is the one chosen on the Connect screen,
    /// or <c>ANDROID_SERIAL</c>, or the only one attached.
    /// </summary>
    public static SessionCoordinator Session => Host.Session;

    /// <summary>Samples the session once a second while it streams: health and figures (D67).</summary>
    public static StreamDiagnostics Diagnostics => Host.Diagnostics;

    /// <summary>
    /// Wi-Fi (D74–D79): this PC's identity (DPAPI, current user) and paired tablets under
    /// %LOCALAPPDATA%\ExtendSpace, and Windows' own DNS-SD for finding tablets. Browsing starts only
    /// when the Wireless connection is chosen.
    /// </summary>
    public static WirelessHost Wireless { get; } = new(
        new HostIdentityStore(Path.Combine(WirelessHost.DefaultFolder, "identity.bin"), new DpapiSecretProtector(), Log),
        new TrustedDeviceStore(Path.Combine(WirelessHost.DefaultFolder, "trusted-devices.json"), Log),
        new DnssdServiceBrowser(log: Log),
        new PairingOptions { AppVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
        Log);

    private static void Log(string line) => System.Diagnostics.Trace.WriteLine(line);

    /// <summary>
    /// Touch from the tablet (D81-D84): onto the mirrored monitor, by device name as the Connect screen
    /// chose it, as real touch or as mouse. Off until the user turns it on.
    /// </summary>
    public static HostInput Input { get; } = new(
        new DesktopMonitorLocator(),
        () => new SyntheticTouchInjector(),
        new SendInputMouseInjector(),
        () => DeviceSelection.MonitorDeviceName,
        Log);

    private static HostSession Host { get; } = HostComposition.CreateHost(
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Environment.GetEnvironmentVariable(HostComposition.DeviceSerialVariable),
        pipeline: new DesktopVideoPipeline(),
        connectionLog: Log,
        selection: DeviceSelection,
        wireless: Wireless,
        input: Input);

    /// <summary>Shared so the selection and its session subscription survive page navigation.</summary>
    private static readonly DisplayTopology Topology = new();

    public static ConnectViewModel ConnectViewModel { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Created here, on the UI thread, so the view model captures the UI synchronization context.
        ConnectViewModel = new ConnectViewModel(
            Session,
            SessionCapabilities.CurrentBuild,
            new AdbDeviceDiscovery(new AdbProcessRunner()),
            DeviceSelection,
            new DisplayCatalog(),
            Diagnostics,
            Wireless,
            Input,
            Topology,
            new VirtualDisplayModes(Topology),
            new WindowRescue(Topology));
        Diagnostics.Start();

        _window = new MainWindow();
        _window.Activate();
    }
}
