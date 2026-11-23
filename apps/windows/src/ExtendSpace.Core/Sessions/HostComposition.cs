using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Sessions;

/// <summary>
/// How the host application puts its session together, in one place so that it can be tested
/// without the WinUI host.
/// </summary>
public static class HostComposition
{
    /// <summary>
    /// adb's own variable for the device it should use when several are attached. The host reads
    /// it for the same purpose; it introduces no setting of its own (section 18.11).
    /// </summary>
    public const string DeviceSerialVariable = "ANDROID_SERIAL";

    /// <summary>
    /// The production session: the coordinator, the real connection over the USB transport (stage
    /// 3), and <paramref name="pipeline"/>. The host application passes the real capture-and-encode
    /// pipeline (<c>ExtendSpace.Media.DesktopVideoPipeline</c>, stage 4), which this assembly cannot
    /// reference because it is Windows-only; left out, the stand-in is used, which fails at the
    /// session's first step, acquiring the display, before adb or a device is reached. Connect is
    /// enabled for USB Mirror by <see cref="SessionCapabilities.CurrentBuild"/> since D65.
    /// </summary>
    /// <param name="deviceSerial">The adb serial to use; null for the only device attached. See <see cref="DeviceSerialVariable"/>.</param>
    /// <param name="connectionLog">Receives the connection's log lines; for acceptance runs and diagnostics.</param>
    /// <param name="selection">The device chosen on the Connect screen (D65); it takes precedence over <paramref name="deviceSerial"/>.</param>
    public static SessionCoordinator CreateSession(
        string appVersion,
        string? deviceSerial = null,
        IAdbCommandRunner? adb = null,
        ISessionClock? clock = null,
        IVideoPipeline? pipeline = null,
        Action<string>? connectionLog = null,
        HostDeviceSelection? selection = null) =>
        CreateHost(appVersion, deviceSerial, adb, clock, pipeline, connectionLog, selection).Session;

    /// <summary>
    /// <see cref="CreateSession"/>, with the stream diagnostics that sample it (D67). The diagnostics
    /// read the pipeline's counters when it implements <see cref="IPipelineDiagnostics"/>; they are
    /// not started here.
    /// </summary>
    public static HostSession CreateHost(
        string appVersion,
        string? deviceSerial = null,
        IAdbCommandRunner? adb = null,
        ISessionClock? clock = null,
        IVideoPipeline? pipeline = null,
        Action<string>? connectionLog = null,
        HostDeviceSelection? selection = null,
        Wireless.WirelessHost? wireless = null,
        Input.HostInput? input = null)
    {
        IVideoPipeline video = pipeline ?? new UnavailableVideoPipeline();
        IHostTransport usb = new UsbTransport(adb ?? new AdbProcessRunner(), new UsbTransportOptions
        {
            DeviceSerial = deviceSerial,
            DeviceSerialSource = selection is null ? null : () => selection.Serial,
        });

        // D75: with wireless, the route is chosen per session from the Connect screen's selection.
        IHostTransport transport = wireless is not null && selection is not null
            ? new RoutedTransport(selection, usb, wireless.CreateTransport(selection))
            : usb;
        var connection = new HostConnectionController(transport, new HostConnectionOptions
        {
            AppVersion = appVersion,
            Log = connectionLog,
            Input = input,
            Extending = selection is null ? null : () => selection.Mode == DisplayMode.Extend,
        });
        var session = new SessionCoordinator(new SessionStateMachine(), connection, video, clock ?? new SystemSessionClock());
        var diagnostics = new StreamDiagnostics(session, connection, video as IPipelineDiagnostics, video.RequestKeyframe, connectionLog);
        return new HostSession(session, diagnostics);
    }
}

/// <summary>The host's session and the diagnostics that observe it.</summary>
public sealed record HostSession(SessionCoordinator Session, StreamDiagnostics Diagnostics);
