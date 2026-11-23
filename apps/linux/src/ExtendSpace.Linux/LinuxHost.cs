using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Linux.Gstreamer;
using ExtendSpace.Linux.Portal;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux;

/// <summary>
/// The Linux host put together (D96): the shared Core session, transports, pairing and input with the
/// Linux platform pieces, the counterpart of the Windows host's App.xaml.cs. Used by the Avalonia UI
/// and by the integration tests alike.
/// </summary>
public sealed class LinuxHost : IDisposable
{
    private uint? _portalSourceTypes;

    private LinuxHost(string appVersion, Action<string> log, LinuxEnvironment environment, string folder)
    {
        Log = log;
        Environment = environment;
        var monitors = new XrandrMonitorSource(environment.X11Display);
        Displays = new X11Displays(monitors);
        Selection = new HostDeviceSelection();
        Wireless = new WirelessHost(
            new HostIdentityStore(Path.Combine(folder, "identity.bin"), new OwnerOnlySecretProtector(folder), log),
            new TrustedDeviceStore(Path.Combine(folder, "trusted-devices.json"), log),
            new MulticastDnsBrowser(log: log),
            new PairingOptions { AppVersion = appVersion },
            log);
        X11VirtualDisplay? x11Virtual = environment.Server == DisplayServer.X11 ? new X11VirtualDisplay(new ProcessCommandRunner(), environment.X11Display) : null;
        HostInput? input = null;
        Sources = new LinuxCaptureSources(environment, monitors, x11Virtual, () => input?.Mode is not null and not InputMode.Off, VirtualSize, log);
        XTestMouse = environment.Server == DisplayServer.X11 ? new XTestMouseInjector(environment.X11Display) : null;
        input = new HostInput(
            new LinuxMonitorLocator(Sources, Displays),
            () => new PortalTouchInjector(Sources),
            new LinuxMouseInjector(Sources, XTestMouse),
            () => Selection.MonitorDeviceName,
            log);
        Input = input;
        Pipeline = new GstVideoPipeline(Sources, log: log);
        Extend = new LinuxExtendStatus(environment, () => _portalSourceTypes, VirtualSize);
        Host = HostComposition.CreateHost(appVersion, System.Environment.GetEnvironmentVariable(HostComposition.DeviceSerialVariable), pipeline: Pipeline, connectionLog: log, selection: Selection, wireless: Wireless, input: Input);
        if (environment.Server == DisplayServer.Wayland)
        {
            _ = Task.Run(async () => _portalSourceTypes = await DesktopPortal.AvailableSourceTypesAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }

    public Action<string> Log { get; }

    public LinuxEnvironment Environment { get; }

    public X11Displays Displays { get; }

    public HostDeviceSelection Selection { get; }

    public WirelessHost Wireless { get; }

    public LinuxCaptureSources Sources { get; }

    public XTestMouseInjector? XTestMouse { get; }

    public HostInput Input { get; }

    public GstVideoPipeline Pipeline { get; }

    public LinuxExtendStatus Extend { get; }

    public HostSession Host { get; }

    public SessionCoordinator Session => Host.Session;

    public StreamDiagnostics Diagnostics => Host.Diagnostics;

    /// <summary>Where the Linux host keeps its identity and trusted devices: <c>$XDG_DATA_HOME/ExtendSpace</c>.</summary>
    public static string DefaultFolder => Path.Combine(
        System.Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share"),
        "ExtendSpace");

    public static LinuxHost Create(string appVersion, Action<string> log, LinuxEnvironment? environment = null, string? folder = null) =>
        new(appVersion, log, environment ?? LinuxEnvironment.Detect(), folder ?? DefaultFolder);

    /// <summary>The Connect screen's view model; create it on the UI thread (it captures the UI context).</summary>
    public ConnectViewModel CreateViewModel() => new(
        Session,
        SessionCapabilities.CurrentBuild,
        new AdbDeviceDiscovery(new AdbProcessRunner()),
        Selection,
        Displays,
        Diagnostics,
        Wireless,
        Input,
        Extend);

    /// <summary>The virtual display's size: the last tablet's shape inside the stream envelope (D92), else 1920 × 1080.</summary>
    public (int Width, int Height) VirtualSize()
    {
        ClientCapabilities? client = Session?.LastClient;
        if (client is { DisplayWidth: > 0, DisplayHeight: > 0 })
        {
            DisplayModeSpec mode = TabletModeAdvisor.CustomSuggestion(new TabletScreen(client.DisplayWidth, client.DisplayHeight, client.RefreshHz));
            return (mode.Width, mode.Height);
        }

        return (1920, 1080);
    }

    public void Dispose()
    {
        Input.Dispose();
        XTestMouse?.Dispose();
        Sources.ReleaseAsync().GetAwaiter().GetResult();
    }
}
