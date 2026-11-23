using ExtendSpace.Core;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Wireless;
using ExtendSpace.Linux.Gstreamer;
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

public sealed record LinuxEnvironment(DisplayServer Server, string Desktop, string? X11Display)
{
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
        return new LinuxEnvironment(server, desktop, x11);
    }
}

/// <summary>
/// The capture sources of a Linux host (D98, D99): on X11 a RandR monitor region (or the virtual area
/// it creates for Extend); on Wayland a ScreenCast portal stream (monitor for Mirror, VIRTUAL for
/// Extend). Holds the session's portal so the input injectors can use it.
/// </summary>
public sealed class LinuxCaptureSources(
    LinuxEnvironment environment,
    IX11MonitorSource monitors,
    X11VirtualDisplay? x11Virtual,
    Func<bool> inputWanted,
    Func<(int Width, int Height)> virtualSize,
    Action<string>? log = null) : ICaptureSourceProvider
{
    private string? _restoreToken;
    private Microsoft.Win32.SafeHandles.SafeFileHandle? _pipeWire;

    /// <summary>The running portal session; null on X11 or between sessions.</summary>
    public DesktopPortal? Portal { get; private set; }

    /// <summary>The rectangle of the X11 virtual monitor while it exists.</summary>
    public ScreenRect? X11VirtualRegion { get; private set; }

    public async Task<(CaptureSource Source, VideoSourceDescription Description)> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation)
    {
        bool extend = request.MonitorDeviceName == LinuxSourceNames.Virtual;
        if (environment.Server == DisplayServer.Wayland)
        {
            (int width, int height) = virtualSize();
            DesktopPortal portal;
            try
            {
                portal = await DesktopPortal.StartAsync(new PortalRequest(extend, inputWanted(), extend ? null : _restoreToken), cancellation).ConfigureAwait(false);
            }
            catch (PortalException e)
            {
                throw new CaptureUnavailableException(e.Cancelled ? "capture_declined" : "portal_failed", e.Cancelled
                    ? "Screen sharing was not allowed in the desktop's dialog."
                    : $"The desktop's screen-sharing portal failed: {e.Message}");
            }

            Portal = portal;
            PortalStream stream = portal.Stream!;
            _restoreToken = stream.RestoreToken ?? _restoreToken;
            _pipeWire = await portal.OpenPipeWireRemoteAsync(cancellation).ConfigureAwait(false);
            int w = stream.Width > 0 ? stream.Width : width;
            int h = stream.Height > 0 ? stream.Height : height;
            log?.Invoke($"portal: stream node {stream.NodeId} {w} x {h} ({(extend ? "virtual" : "monitor")})");
            return (new CaptureSource.PipeWireNode((int)_pipeWire.DangerousGetHandle(), stream.NodeId, extend ? width : null, extend ? height : null), new VideoSourceDescription(w, h, 60));
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

    public async Task ReleaseAsync()
    {
        if (Portal is { } portal)
        {
            Portal = null;
            await portal.DisposeAsync().ConfigureAwait(false);
        }

        _pipeWire?.Dispose();
        _pipeWire = null;
        if (X11VirtualRegion is not null)
        {
            X11VirtualRegion = null;
            x11Virtual?.Remove();
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

/// <summary>Mouse mode: RemoteDesktop portal pointer on Wayland, XTest on X11 (D100). Mouse emulation either way.</summary>
public sealed class LinuxMouseInjector(LinuxCaptureSources sources, XTestMouseInjector? xtest) : IMouseInjector
{
    private const int ButtonLeft = 0x110;
    private const int ButtonRight = 0x111;

    public void Move(ScreenPoint point)
    {
        if (sources.Portal is { CanControl: true } portal)
        {
            portal.PointerMotion(point.X, point.Y);
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
        else
        {
            xtest?.Button(button, down);
        }
    }

    public void Wheel(int vertical, int horizontal)
    {
        if (sources.Portal is { CanControl: true } portal)
        {
            // Windows-style wheel units (positive = away from the user); evdev's positive vertical is down.
            if (vertical / 120 != 0)
            {
                portal.PointerAxisDiscrete(0, -vertical / 120);
            }

            if (horizontal / 120 != 0)
            {
                portal.PointerAxisDiscrete(1, horizontal / 120);
            }
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
            case DisplayServer.Wayland when environment.IsWlroots:
                return new ExtendStatus(ExtendReadiness.NoVirtualDisplay, null,
                    "On Sway and other wlroots desktops ExtendSpace cannot create a display. Run 'swaymsg create_output', then choose Mirror and pick HEADLESS-1 in the sharing dialog.");
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
