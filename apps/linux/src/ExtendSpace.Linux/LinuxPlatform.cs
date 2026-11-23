using ExtendSpace.Core;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Linux.Gstreamer;
using ExtendSpace.Linux.Hyprland;
using ExtendSpace.Linux.Wayland;
using ExtendSpace.Linux.Portal;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux;

/// <summary>Which display server and desktop the host runs in (D98).</summary>
public enum DisplayServer
{
    None,
    X11,
    Wayland,
}

public sealed record LinuxEnvironment(DisplayServer Server, string Desktop, string? X11Display, bool HyprlandRunning = false)
{
    /// <summary>Hyprland, recognised by its instance signature (set for every client it starts) or its desktop name.</summary>
    public bool IsHyprland => Server == DisplayServer.Wayland && (HyprlandRunning || Desktop.Contains("Hyprland", StringComparison.OrdinalIgnoreCase));

    public bool IsGnomeOrKde => Desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) || Desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

    public bool IsWlroots => Desktop.Contains("sway", StringComparison.OrdinalIgnoreCase) || Desktop.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) || Desktop.Contains("wlroots", StringComparison.OrdinalIgnoreCase);

    public string Describe => Server switch
    {
        DisplayServer.Wayland => $"{(Desktop.Length == 0 ? "a Wayland desktop" : Desktop)} (Wayland)",
        DisplayServer.X11 => $"{(Desktop.Length == 0 ? "an X11 desktop" : Desktop)} (X11)",
        _ => "no graphical session",
    };

    /// <summary>Reads the standard session variables. Wayland wins when both are set (XWayland sets DISPLAY too).</summary>
    public static LinuxEnvironment Detect(Func<string, string?>? variable = null)
    {
        variable ??= Environment.GetEnvironmentVariable;
        string type = variable("XDG_SESSION_TYPE") ?? string.Empty;
        string desktop = variable("XDG_CURRENT_DESKTOP") ?? variable("DESKTOP_SESSION") ?? string.Empty;
        string? x11 = variable("DISPLAY");
        DisplayServer server = !string.IsNullOrEmpty(variable("WAYLAND_DISPLAY")) || type.Equals("wayland", StringComparison.OrdinalIgnoreCase)
            ? DisplayServer.Wayland
            : !string.IsNullOrEmpty(x11) ? DisplayServer.X11 : DisplayServer.None;
        return new LinuxEnvironment(server, desktop, x11, !string.IsNullOrEmpty(variable("HYPRLAND_INSTANCE_SIGNATURE")));
    }
}

/// <summary>
/// Restore tokens of the ScreenCast portal, one per output (D105): the dialog is skipped for an output
/// already chosen once, and a token never brings back a different output than the one asked for. Kept
/// in the host's owner-only folder so a restart does not ask again. Not secrets: a token only works
/// for this application in this user's session.
/// </summary>
public sealed class PortalTokenStore(string? path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _tokens;

    public string? Get(string key)
    {
        lock (_gate)
        {
            return Load().TryGetValue(key, out string? token) ? token : null;
        }
    }

    public void Set(string key, string? token)
    {
        lock (_gate)
        {
            Dictionary<string, string> tokens = Load();
            if (token is null ? !tokens.Remove(key) : tokens.TryGetValue(key, out string? old) && old == token)
            {
                return;
            }

            if (token is not null)
            {
                tokens[key] = token;
            }

            if (path is not null)
            {
                try
                {
                    // Reflection-free (trimmed and AOT hosts disable reflection-based JSON).
                    using var stream = new MemoryStream();
                    using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
                    {
                        writer.WriteStartObject();
                        foreach ((string name, string value) in tokens)
                        {
                            writer.WriteString(name, value);
                        }

                        writer.WriteEndObject();
                    }

                    File.WriteAllBytes(path, stream.ToArray());
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A token not saved only means the dialog is shown again next time.
                }
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_tokens is null)
        {
            _tokens = [];
            if (path is not null && File.Exists(path))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                    if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
                        {
                            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                _tokens[property.Name] = property.Value.GetString()!;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    // An unreadable file only means the dialog is shown again.
                }
            }
        }

        return _tokens;
    }
}

/// <summary>What the Wayland desktop offers for input, read once at start (D104, D106).</summary>
public sealed record WaylandInputSupport(bool RemoteDesktopPortal, bool VirtualPointer);

/// <summary>
/// The capture sources of a Linux host (D98, D99, D105): on X11 a RandR monitor region (or the virtual
/// area it creates for Extend); on Wayland a ScreenCast portal stream (a monitor for Mirror; for Extend
/// the portal's VIRTUAL source on GNOME/KDE, or a Hyprland headless output captured as a monitor).
/// Holds the session's portal and, where it is the input path, its virtual pointer.
/// </summary>
public sealed class LinuxCaptureSources(
    LinuxEnvironment environment,
    IX11MonitorSource monitors,
    X11VirtualDisplay? x11Virtual,
    Func<bool> inputWanted,
    Func<(int Width, int Height)> virtualSize,
    Action<string>? log = null,
    HyprlandVirtualDisplay? hyprland = null,
    WaylandInputSupport? input = null,
    PortalTokenStore? tokens = null,
    Func<string, double>? outputScale = null) : ICaptureSourceProvider
{
    /// <summary>The token key of a portal without <c>mapping_id</c> (GNOME, KDE): one monitor choice.</summary>
    private const string AnyMonitor = "monitor";
    private readonly PortalTokenStore _tokens = tokens ?? new PortalTokenStore(null);
    private readonly Lock _pointerGate = new();
    private Microsoft.Win32.SafeHandles.SafeFileHandle? _pipeWire;
    private VirtualPointer? _pointer;
    private bool _pointerFailed;

    /// <summary>The running portal session; null on X11 or between sessions.</summary>
    public DesktopPortal? Portal { get; private set; }

    /// <summary>The rectangle of the X11 virtual monitor while it exists.</summary>
    public ScreenRect? X11VirtualRegion { get; private set; }

    /// <summary>Physical pixels per logical pixel of the streamed output (1 when unknown): scrolling is in logical pixels.</summary>
    public double OutputScale { get; private set; } = 1;

    /// <summary>The Hyprland output made for Extend while it exists.</summary>
    public string? VirtualOutput { get; private set; }

    public async Task<(CaptureSource Source, VideoSourceDescription Description)> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation)
    {
        bool extend = request.MonitorDeviceName == LinuxSourceNames.Virtual;
        if (environment.Server == DisplayServer.Wayland)
        {
            return await AcquireWaylandAsync(request, extend, cancellation).ConfigureAwait(false);
        }

        if (environment.Server != DisplayServer.X11 || environment.X11Display is null)
        {
            throw new CaptureUnavailableException("no_display", "There is no graphical session to capture (neither Wayland nor X11).");
        }

        ScreenRect region;
        if (extend)
        {
            if (x11Virtual is null)
            {
                throw new CaptureUnavailableException("no_virtual_display", "Extend is not available in this session.");
            }

            (int width, int height) = virtualSize();
            try
            {
                region = x11Virtual.Create(width, height);
            }
            catch (InvalidOperationException e)
            {
                throw new CaptureUnavailableException("virtual_display_failed", $"The virtual display could not be added: {e.Message}");
            }

            X11VirtualRegion = region;
            log?.Invoke($"x11: virtual display {region}");
        }
        else
        {
            IReadOnlyList<X11Monitor> list = monitors.Monitors() ?? [];
            X11Monitor? monitor = request.MonitorDeviceName is null
                ? list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault()
                : list.FirstOrDefault(m => m.Name == request.MonitorDeviceName);
            region = monitor?.Bounds ?? throw new CaptureUnavailableException("display_not_found", $"The display {request.MonitorDeviceName} is not connected.");
        }

        return (new CaptureSource.X11Region(environment.X11Display, region), new VideoSourceDescription(region.Width, region.Height, 60));
    }

    private async Task<(CaptureSource Source, VideoSourceDescription Description)> AcquireWaylandAsync(VideoSourceRequest request, bool extend, CancellationToken cancellation)
    {
        (int width, int height) = virtualSize();
        // The main display is asked for without a name (D68); where output names are the portal's own
        // (Hyprland), it is resolved, so that its restore token is found and the shared output checked.
        string? expected = extend ? null : request.MonitorDeviceName
            ?? (environment.IsHyprland ? (monitors.Monitors() ?? []).FirstOrDefault(m => m.IsPrimary)?.Name : null);
        if (extend && hyprland is not null)
        {
            try
            {
                HyprlandMonitor made = await Task.Run(() => hyprland.Create(width, height), cancellation).ConfigureAwait(false);
                VirtualOutput = made.Name;
                expected = made.Name;
            }
            catch (InvalidOperationException e)
            {
                throw new CaptureUnavailableException("virtual_display_failed", $"The virtual display could not be added: {e.Message}");
            }
        }

        // GNOME and KDE make the virtual monitor themselves (portal VIRTUAL); everything else is a monitor.
        bool portalVirtual = extend && hyprland is null;
        bool remote = inputWanted() && (input?.RemoteDesktopPortal ?? true);
        string? token = portalVirtual ? null : (expected is null ? null : _tokens.Get(expected)) ?? _tokens.Get(AnyMonitor);
        DesktopPortal portal;
        try
        {
            portal = await DesktopPortal.StartAsync(new PortalRequest(portalVirtual, remote, token), cancellation).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RemoveVirtualOutput();
            if (e is PortalException p)
            {
                throw new CaptureUnavailableException(p.Cancelled ? "capture_declined" : "portal_failed", p.Cancelled
                    ? "Screen sharing was not allowed in the desktop's dialog."
                    : $"The desktop's screen-sharing portal failed: {p.Message}");
            }

            throw;
        }

        PortalStream stream = portal.Stream!;
        if (expected is not null && stream.Output is { } shown && shown != expected)
        {
            // D68: never another display than the one chosen. The user picked something else in the dialog.
            await portal.DisposeAsync().ConfigureAwait(false);
            RemoveVirtualOutput();
            throw new CaptureUnavailableException("wrong_display", extend
                ? $"In the desktop's sharing dialog choose {expected}, the display ExtendSpace added for the tablet ({shown} was chosen). Connect again."
                : $"You chose {expected} here but {shown} in the desktop's sharing dialog. Connect again and choose {expected}, or choose {shown} here.");
        }

        Portal = portal;
        OutputScale = stream.Output is { } named && outputScale?.Invoke(named) is > 0 and var scale ? scale : 1;
        if (!portalVirtual)
        {
            _tokens.Set(stream.Output ?? AnyMonitor, stream.RestoreToken);
        }

        _pipeWire = await portal.OpenPipeWireRemoteAsync(cancellation).ConfigureAwait(false);
        int w = stream.Width > 0 ? stream.Width : width;
        int h = stream.Height > 0 ? stream.Height : height;
        log?.Invoke($"portal: stream node {stream.NodeId} {w} x {h} of {stream.Output ?? "an unnamed output"} ({(extend ? "virtual" : "monitor")}{(token is null ? string.Empty : ", restored without the dialog")}){(remote ? ", remote control" : string.Empty)}");
        return (new CaptureSource.PipeWireNode((int)_pipeWire.DangerousGetHandle(), stream.NodeId, portalVirtual ? width : null, portalVirtual ? height : null), new VideoSourceDescription(w, h, 60));
    }

    /// <summary>
    /// The virtual pointer on the streamed output, made at the first input and kept for the session
    /// (D104); null where it is not the input path or cannot be made.
    /// </summary>
    public VirtualPointer? Pointer()
    {
        if (input is not { VirtualPointer: true } || Portal?.Stream?.Output is not { } output)
        {
            return null;
        }

        lock (_pointerGate)
        {
            if (_pointer is { IsAlive: true } alive && alive.BoundOutput == output)
            {
                return alive;
            }

            _pointer?.Dispose();
            _pointer = null;
            try
            {
                _pointer = VirtualPointer.Create(output);
                _pointerFailed = false;
                log?.Invoke($"input: virtual pointer on {output}");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
            {
                if (!_pointerFailed)
                {
                    _pointerFailed = true;
                    log?.Invoke($"input: no virtual pointer: {e.Message}");
                }
            }

            return _pointer;
        }
    }

    public async Task ReleaseAsync()
    {
        lock (_pointerGate)
        {
            // First, so a held button is released while the output still exists.
            _pointer?.Dispose();
            _pointer = null;
            _pointerFailed = false;
        }

        if (Portal is { } portal)
        {
            Portal = null;
            await portal.DisposeAsync().ConfigureAwait(false);
        }

        _pipeWire?.Dispose();
        _pipeWire = null;
        RemoveVirtualOutput();
        if (X11VirtualRegion is not null)
        {
            X11VirtualRegion = null;
            x11Virtual?.Remove();
        }
    }

    private void RemoveVirtualOutput()
    {
        if (hyprland is not null && VirtualOutput is not null)
        {
            VirtualOutput = null;
            hyprland.Remove();
        }
    }
}

/// <summary>Input aims at the stream on Wayland (portal coordinates), at the RandR monitor on X11.</summary>
public sealed class LinuxMonitorLocator(LinuxCaptureSources sources, X11Displays x11) : IMonitorLocator
{
    public ScreenRect? Locate(string? deviceName)
    {
        if (sources.Portal?.Stream is { } stream)
        {
            return new ScreenRect(0, 0, stream.Width > 0 ? stream.Width : 1, stream.Height > 0 ? stream.Height : 1);
        }

        if (deviceName == LinuxSourceNames.Virtual)
        {
            return sources.X11VirtualRegion;
        }

        return x11.Locate(deviceName);
    }
}

/// <summary>
/// Mouse mode (D100, D104): the RemoteDesktop portal's pointer where the desktop has one (GNOME, KDE),
/// the compositor's virtual pointer on the streamed output on Hyprland and wlroots, XTest on X11.
/// Mouse emulation either way. Points are in the stream's pixels on Wayland.
/// </summary>
public sealed class LinuxMouseInjector(LinuxCaptureSources sources, XTestMouseInjector? xtest) : IMouseInjector, IPreciseScrollInjector
{
    private const int ButtonLeft = 0x110;
    private const int ButtonRight = 0x111;
    private double _notchY;
    private double _notchX;

    /// <summary>D107: smooth on Wayland (portal axis, virtual-pointer finger axis); notches on X11.</summary>
    public void ScrollPixels(double vertical, double horizontal)
    {
        double scale = sources.OutputScale;
        // Core's sign is the wheel's (positive = view towards the top); Wayland's positive scrolls down.
        double down = -vertical / scale;
        double right = horizontal / scale;
        if (sources.Portal is { CanControl: true } portal)
        {
            portal.PointerAxis(right, down, finish: false);
        }
        else if (sources.Pointer() is { } pointer)
        {
            pointer.ScrollSmooth(down, right);
        }
        else if (xtest is not null)
        {
            _notchY += vertical;
            _notchX += horizontal;
            int y = (int)(_notchY / HostInput.ScrollPixelsPerNotch);
            int x = (int)(_notchX / HostInput.ScrollPixelsPerNotch);
            if (y != 0 || x != 0)
            {
                xtest.Wheel(y * 120, x * 120);
                _notchY -= y * HostInput.ScrollPixelsPerNotch;
                _notchX -= x * HostInput.ScrollPixelsPerNotch;
            }
        }
    }

    public void ScrollEnd()
    {
        _notchX = _notchY = 0;
        if (sources.Portal is { CanControl: true } portal)
        {
            portal.PointerAxis(0, 0, finish: true);
        }
        else if (sources.Pointer() is { } pointer)
        {
            pointer.ScrollStop();
        }
    }

    public void Move(ScreenPoint point)
    {
        if (sources.Portal is { CanControl: true } portal)
        {
            portal.PointerMotion(point.X, point.Y);
        }
        else if (sources.Pointer() is { } pointer && sources.Portal?.Stream is { } stream)
        {
            pointer.MoveTo(point.X, point.Y, Math.Max(1, stream.Width), Math.Max(1, stream.Height));
        }
        else
        {
            xtest?.Move(point);
        }
    }

    public void Button(MouseButton button, bool down)
    {
        if (sources.Portal is { CanControl: true } portal)
        {
            portal.PointerButton(button == MouseButton.Left ? ButtonLeft : ButtonRight, down);
        }
        else if (sources.Pointer() is { } pointer)
        {
            pointer.Button(button == MouseButton.Right, down);
        }
        else
        {
            xtest?.Button(button, down);
        }
    }

    public void Wheel(int vertical, int horizontal)
    {
        // Windows-style wheel units (positive = away from the user); evdev's positive vertical is down.
        int down = -vertical / 120;
        int right = horizontal / 120;
        if (sources.Portal is { CanControl: true } portal)
        {
            if (down != 0)
            {
                portal.PointerAxisDiscrete(0, down);
            }

            if (right != 0)
            {
                portal.PointerAxisDiscrete(1, right);
            }
        }
        else if (sources.Pointer() is { } pointer)
        {
            pointer.Scroll(0, down);
            pointer.Scroll(1, right);
        }
        else
        {
            xtest?.Wheel(vertical, horizontal);
        }
    }
}

/// <summary>
/// Touch mode through the RemoteDesktop portal (D100): genuine touch events in the compositor, in the
/// stream's coordinates, so they can only land on the shared monitor. Refused (false) where there is
/// no portal control (X11, wlroots): ExtendSpace does not use uinput.
/// </summary>
public sealed class PortalTouchInjector(LinuxCaptureSources sources) : ITouchInjector
{
    public bool Inject(IReadOnlyList<InjectedContact> frame)
    {
        if (sources.Portal is not { CanControl: true } portal)
        {
            return false;
        }

        foreach (InjectedContact contact in frame)
        {
            switch (contact.State)
            {
                case ContactState.Down:
                    portal.TouchDown(contact.PointerId, contact.Point.X, contact.Point.Y);
                    break;
                case ContactState.Update:
                    portal.TouchMotion(contact.PointerId, contact.Point.X, contact.Point.Y);
                    break;
                default:
                    portal.TouchUp(contact.PointerId);
                    break;
            }
        }

        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Extend's readiness on Linux (D98, D99), which depends on the environment rather than on a driver.
/// The virtual display exists only during a session, so the target is a placeholder name.
/// </summary>
public sealed class LinuxExtendStatus(LinuxEnvironment environment, Func<uint?> portalSourceTypes, Func<(int Width, int Height)> virtualSize) : IVirtualDisplayCatalog, IExtendStatusSource
{
    public IReadOnlyList<DisplayTarget> Targets() => [];

    public ExtendStatus Status()
    {
        (int width, int height) = virtualSize();
        var target = new VirtualDisplay(new DisplayTarget(LinuxSourceNames.Virtual, "ExtendSpace virtual display", null, null, 0, true, new ScreenRect(0, 0, width, height)), VirtualDisplayProvider.Unknown);
        switch (environment.Server)
        {
            case DisplayServer.Wayland when environment.IsHyprland:
                return new ExtendStatus(ExtendReadiness.Ready, target,
                    $"When you connect, ExtendSpace adds a {width} × {height} display at 60 Hz ({HyprlandVirtualDisplay.OutputName}) to the right of your screens; Hyprland treats it as a monitor. The first time, choose Monitor → {HyprlandVirtualDisplay.OutputName} in the sharing dialog. It is removed when the tablet disconnects.", 1);
            case DisplayServer.Wayland when environment.IsWlroots:
                return new ExtendStatus(ExtendReadiness.NoVirtualDisplay, null,
                    "On Sway and other wlroots desktops ExtendSpace cannot create a display yet. Run 'swaymsg create_output', then choose Mirror and pick HEADLESS-1 in the sharing dialog.");
            case DisplayServer.Wayland:
                uint? types = portalSourceTypes();
                return types is { } t && (t & DesktopPortal.SourceVirtual) != 0
                    ? new ExtendStatus(ExtendReadiness.Ready, target,
                        $"When you connect, {environment.Describe} asks to share a new virtual display ({width} × {height}). It exists only while the tablet is connected; move windows onto it as onto any display.", 1)
                    : new ExtendStatus(ExtendReadiness.NoVirtualDisplay, null, types is null
                        ? "The desktop's screen-sharing portal is not available. Install xdg-desktop-portal and your desktop's portal (gnome or kde)."
                        : $"{environment.Describe} cannot create virtual displays through its screen-sharing portal. GNOME and KDE Plasma can.");
            case DisplayServer.X11:
                return new ExtendStatus(ExtendReadiness.Ready, target,
                    $"When you connect, ExtendSpace adds a {width} × {height} display to the right of your screens and removes it afterwards. Experimental on X11: some window managers do not treat it as a monitor.", 1);
            default:
                return new ExtendStatus(ExtendReadiness.NoVirtualDisplay, null, "There is no graphical session.");
        }
    }
}

/// <summary>
/// What input a Linux session can deliver (D106), so the screen never offers a mode that silently does
/// nothing. Touch needs the RemoteDesktop portal (GNOME, KDE): no other safe path injects real touch,
/// and ExtendSpace does not use uinput or root. Mouse also works through the compositor's virtual
/// pointer (Hyprland, wlroots) and XTest (X11).
/// </summary>
public sealed class LinuxInputAvailability(LinuxEnvironment environment, WaylandInputSupport support) : IInputAvailability
{
    private string Desktop => environment.IsHyprland ? "Hyprland" : environment.Describe;

    public string? Unavailable(InputMode mode) => (mode, environment.Server) switch
    {
        (InputMode.Off, _) => null,
        (InputMode.Touch, DisplayServer.Wayland) when support.RemoteDesktopPortal => null,
        (InputMode.Touch, DisplayServer.X11) => "Not available on X11 sessions: there is no safe way to inject touch. Use Mouse.",
        (InputMode.Touch, _) => $"Not available on {Desktop}: its desktop portal has no remote control, the only safe way to inject touch. Use Mouse.",
        (InputMode.Mouse, DisplayServer.X11) => null,
        (InputMode.Mouse, DisplayServer.Wayland) when support.RemoteDesktopPortal || support.VirtualPointer => null,
        (InputMode.Mouse, _) => $"Not available on {Desktop}: it offers neither a remote-control portal nor a virtual pointer.",
        _ => null,
    };

    public string? Note => environment.Server switch
    {
        DisplayServer.Wayland when support.RemoteDesktopPortal => "Your desktop asks once to allow remote control. Input lands only on the shared display.",
        DisplayServer.Wayland when support.VirtualPointer => $"On {Desktop} the tablet moves the pointer through the compositor's virtual-pointer protocol, only on the shared display and only while connected. No dialog asks for this.",
        DisplayServer.X11 => "On X11 the tablet works as a mouse.",
        _ => null,
    };
}

/// <summary>
/// Identity at rest on Linux (ES-151): the bytes are stored as they are, in a folder only the user can
/// read (0700, files 0600 by the folder), like SSH keys. Weaker than DPAPI against an offline copy of
/// the home folder; a Secret Service keyring is the follow-up.
/// </summary>
public sealed class OwnerOnlySecretProtector : ISecretProtector
{
    public OwnerOnlySecretProtector(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public byte[] Protect(byte[] secret) => [.. secret];

    public byte[] Unprotect(byte[] protectedData) => [.. protectedData];
}
