using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Linux.Gstreamer;
using ExtendSpace.Linux.Hyprland;
using ExtendSpace.Linux.Wayland;
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
    private readonly string _lastTabletPath;
    private TabletScreen? _lastTablet;

    private LinuxHost(string appVersion, Action<string> log, LinuxEnvironment environment, string folder)
    {
        Log = log;
        Environment = environment;
        _lastTabletPath = Path.Combine(folder, "last-tablet.json");
        _lastTablet = LoadTablet(_lastTabletPath);
        var runner = new ProcessCommandRunner();
        // D105: on Hyprland the outputs come from the compositor, named as the portal names them.
        IX11MonitorSource monitors = environment.IsHyprland ? new HyprlandMonitorSource(runner) : new XrandrMonitorSource(environment.X11Display);
        Displays = new X11Displays(monitors);
        Selection = new HostDeviceSelection();
        Wireless = new WirelessHost(
            new HostIdentityStore(Path.Combine(folder, "identity.bin"), new OwnerOnlySecretProtector(folder), log),
            new TrustedDeviceStore(Path.Combine(folder, "trusted-devices.json"), log),
            new MulticastDnsBrowser(log: log),
            new PairingOptions { AppVersion = appVersion },
            log);
        X11VirtualDisplay? x11Virtual = environment.Server == DisplayServer.X11 ? new X11VirtualDisplay(runner, environment.X11Display) : null;
        HyprlandVirtualDisplay? hyprland = environment.IsHyprland ? new HyprlandVirtualDisplay(runner, log) : null;
        // A virtual output left by a crashed or killed run is removed before anything else (no ghost display).
        hyprland?.RemoveLeftovers();
        InputSupport = environment.Server == DisplayServer.Wayland
            ? new WaylandInputSupport(DesktopPortal.HasRemoteDesktopAsync(CancellationToken.None).GetAwaiter().GetResult(), VirtualPointer.IsAvailable())
            : new WaylandInputSupport(false, false);
        InputAvailability = new LinuxInputAvailability(environment, InputSupport);
        log($"input paths: remote-desktop portal {(InputSupport.RemoteDesktopPortal ? "yes" : "no")}, virtual pointer {(InputSupport.VirtualPointer ? "yes" : "no")}");
        HostInput? input = null;
        Sources = new LinuxCaptureSources(environment, monitors, x11Virtual, () => input?.Mode is not null and not InputMode.Off, VirtualSize, log, hyprland, InputSupport, new PortalTokenStore(Path.Combine(folder, "portal-tokens.json")),
            environment.IsHyprland ? name => Hyprctl.Monitors(runner)?.FirstOrDefault(m => m.Name == name)?.Scale ?? 1 : null);
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
        // The Extend display is created before the handshake tells the tablet's shape, so the last
        // tablet's screen is remembered (ES-162): the first Extend after a start is already tablet-shaped.
        Host.Session.StateChanged += (_, _) => RememberTablet(Host.Session.LastClient);
        if (environment.Server == DisplayServer.Wayland)
        {
            _ = Task.Run(async () => _portalSourceTypes = await DesktopPortal.AvailableSourceTypesAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }

    public Action<string> Log { get; }

    public LinuxEnvironment Environment { get; }

    public WaylandInputSupport InputSupport { get; }

    public LinuxInputAvailability InputAvailability { get; }

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
        Extend,
        inputAvailability: InputAvailability);

    /// <summary>
    /// The virtual display's size: the tablet's shape inside the stream envelope (D92) — this session's
    /// tablet, else the last one remembered — else 1920 × 1080.
    /// </summary>
    public (int Width, int Height) VirtualSize()
    {
        ClientCapabilities? client = Session?.LastClient;
        TabletScreen? tablet = client is { DisplayWidth: > 0, DisplayHeight: > 0 }
            ? new TabletScreen(client.DisplayWidth, client.DisplayHeight, client.RefreshHz)
            : _lastTablet;
        if (tablet is not null)
        {
            DisplayModeSpec mode = TabletModeAdvisor.CustomSuggestion(tablet);
            return (mode.Width, mode.Height);
        }

        return (1920, 1080);
    }

    internal static TabletScreen? LoadTablet(string path)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            System.Text.Json.JsonElement root = document.RootElement;
            int width = root.GetProperty("width").GetInt32();
            int height = root.GetProperty("height").GetInt32();
            double refresh = root.TryGetProperty("refreshHz", out System.Text.Json.JsonElement r) ? r.GetDouble() : 60;
            return width > 0 && height > 0 ? new TabletScreen(width, height, refresh) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void RememberTablet(ClientCapabilities? client)
    {
        if (client is not { DisplayWidth: > 0, DisplayHeight: > 0 })
        {
            return;
        }

        var tablet = new TabletScreen(client.DisplayWidth, client.DisplayHeight, client.RefreshHz);
        if (tablet == _lastTablet)
        {
            return;
        }

        _lastTablet = tablet;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("width", tablet.Width);
                writer.WriteNumber("height", tablet.Height);
                writer.WriteNumber("refreshHz", tablet.RefreshHz);
                writer.WriteEndObject();
            }

            File.WriteAllBytes(_lastTabletPath, stream.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        Input.Dispose();
        XTestMouse?.Dispose();
        Sources.ReleaseAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// The one-instance-per-user lock (ES-164): an exclusive lock on a file in the host's folder. .NET takes
/// an advisory <c>flock</c> for <see cref="FileShare.None"/> on Linux, which the kernel drops with the
/// process, so a crash never leaves the lock behind.
/// </summary>
public static class InstanceLock
{
    /// <summary>The held lock, or null when another process holds it.</summary>
    public static FileStream? TryAcquire(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            return new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
