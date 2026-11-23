using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Display;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.ViewModels;

/// <summary>One selectable transport or display mode as shown on the Connect screen.</summary>
/// <param name="StatusNote">Why the option cannot be used in this build, or empty when it can.</param>
public sealed record ConnectOption(string Title, string Description, string StatusNote)
{
    // List controls fall back to ToString() for the accessible name of a templated item.
    public override string ToString() => Title;
}

/// <summary>One Android device as listed on the Connect screen (D65).</summary>
/// <param name="Serial">As adb names it. Shown, because it is the only thing that tells two of the same model apart.</param>
/// <param name="Status">"Ready", or what the user has to do first.</param>
/// <param name="Label">What identifies it in the list when <paramref name="Serial"/> is not meant for people (a Wi-Fi tablet's short ID).</param>
/// <param name="WirelessDevice">For a Wi-Fi tablet: what discovery and the trust list say about it.</param>
public sealed record DeviceOption(string Serial, string Title, string Status, bool IsUsable, string? Label = null, WirelessDeviceEntry? WirelessDevice = null)
{
    public string Detail => $"{Label ?? Serial} · {Status}";

    public override string ToString() => Title;

    internal static DeviceOption From(AdbDevice device) => new(
        device.Serial,
        device.Model ?? "Android device",
        device.State switch
        {
            "device" => "Ready",
            "unauthorized" => "Allow USB debugging on the device, then wait a moment",
            "offline" => "Not responding: reconnect the cable",
            "authorizing" => "Waiting for the device to authorise this PC",
            _ => $"Not ready ({device.State})",
        },
        device.IsUsable);

    internal static DeviceOption From(WirelessDeviceEntry device) => new(
        device.Key,
        device.Name,
        device switch
        {
            { IsCompatible: false } => "Needs a newer version of ExtendSpace",
            { IsPaired: true, IsLastKnown: true } => $"Paired · at its last address {device.EndPoint!.Address}",
            { IsPaired: true, IsAvailable: true } => "Paired · ready",
            { IsPaired: true } => "Paired · not found on the network",
            { IsManual: true } => "Added by address · not paired",
            { PairingOpen: true } => "Not paired · ready to pair",
            _ => "Not paired",
        },
        device is { IsPaired: true, IsAvailable: true, IsCompatible: true },
        device.DeviceId is { } id ? $"Wi-Fi {DeviceIds.Short(id)}" : device.IdPrefix is { } prefix ? $"Wi-Fi {DeviceIds.Short(prefix)}" : "Wi-Fi",
        device);
}

/// <summary>One monitor as listed on the Connect screen (D68).</summary>
public sealed record DisplayOption(string DeviceName, string Title, string Detail, bool IsPrimary)
{
    public override string ToString() => Title;

    internal static DisplayOption From(DisplayInfo display) => new(
        display.DeviceName,
        display.IsPrimary ? $"{DisplayInfo.LabelFor(display.DeviceName)} (main)" : DisplayInfo.LabelFor(display.DeviceName),
        display.RefreshHz is { } hz ? $"{display.Width} × {display.Height}, {hz} Hz" : $"{display.Width} × {display.Height}",
        display.IsPrimary);
}

/// <summary>
/// State and intent for the Connect screen. Holds no transport or streaming logic: intent goes to
/// the session's owner (<see cref="SessionCoordinator"/> in the application), which is the only
/// thing that changes session state.
///
/// <para><b>Devices (D65).</b> With a <see cref="IDeviceDiscovery"/>, the screen lists the attached
/// Android devices (refreshed by the page while a device can be chosen), keeps the user's choice
/// across refreshes by serial, chooses the only usable device by itself, and enables Connect only
/// for a usable chosen device. The choice is handed to the transport through
/// <see cref="HostDeviceSelection"/> when the session starts; the transport checks the device
/// again, because the list can be out of date.</para>
/// </summary>
public sealed class ConnectViewModel : ObservableObject
{
    private readonly ISessionControl _session;
    private readonly SessionCapabilities _capabilities;
    private readonly IDeviceDiscovery? _discovery;
    private readonly HostDeviceSelection? _selection;
    private readonly IDisplayCatalog? _displays;
    private readonly StreamDiagnostics? _diagnostics;
    private readonly WirelessHost? _wireless;
    private readonly Input.HostInput? _input;
    private readonly IVirtualDisplayCatalog? _virtualDisplays;
    private ExtendStatus? _extendStatus;
    private readonly IVirtualDisplayModes? _modes;
    private readonly IWindowRescue? _rescue;
    private readonly Func<ClientCapabilities?> _lastClient;
    private ModeAdvice? _modeAdvice;
    private string? _displayActionResult;
    private IReadOnlyList<DeviceOption> _usbDevices = [];
    private IReadOnlyList<DeviceOption> _wirelessDevices = [];
    private string? _pairingStatus;
    private bool _pairing;
    private string _usbStatus = "Looking for Android devices…";
    private IReadOnlyList<DisplayOption> _displayOptions = [];
    private DisplayOption? _selectedDisplay;
    private string? _streamHealth;
    private bool _streamHealthIsProblem;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private TransportKind _selectedTransport = TransportKind.Usb;
    private DisplayMode _selectedMode = DisplayMode.Mirror;
    private IReadOnlyList<DeviceOption> _devices = [];
    private DeviceOption? _selectedDevice;
    private string _devicesStatus = "Looking for Android devices…";
    private bool _devicesListed;
    private int _refreshing;
    private readonly Input.IInputAvailability? _inputAvailability;

    public ConnectViewModel(
        ISessionControl session,
        SessionCapabilities capabilities,
        IDeviceDiscovery? discovery = null,
        HostDeviceSelection? selection = null,
        IDisplayCatalog? displays = null,
        StreamDiagnostics? diagnostics = null,
        WirelessHost? wireless = null,
        Input.HostInput? input = null,
        IVirtualDisplayCatalog? virtualDisplays = null,
        IVirtualDisplayModes? virtualDisplayModes = null,
        IWindowRescue? windowRescue = null,
        Func<ClientCapabilities?>? lastClient = null,
        Input.IInputAvailability? inputAvailability = null)
    {
        _inputAvailability = inputAvailability;
        _virtualDisplays = virtualDisplays;
        _modes = virtualDisplayModes;
        _rescue = windowRescue;
        _lastClient = lastClient ?? (() => (session as SessionCoordinator)?.LastClient);
        _wireless = wireless;
        _input = input;
        InputOptions =
        [
            new("Off", "The tablet only shows the picture.", string.Empty),
            new("Touch", "Touches on the tablet act as a touchscreen on the mirrored display.", inputAvailability?.Unavailable(Input.InputMode.Touch) ?? string.Empty),
            new("Mouse", "Tap to click, drag, hold for a right-click, two fingers to scroll.", inputAvailability?.Unavailable(Input.InputMode.Mouse) ?? string.Empty),
        ];
        if (wireless is not null)
        {
            wireless.Directory.Changed += (_, _) => OnUi(RefreshWirelessDevices);
        }

        _session = session;
        _capabilities = capabilities;
        _discovery = discovery;
        _selection = selection;
        _displays = displays;
        _diagnostics = diagnostics;
        if (diagnostics is not null)
        {
            diagnostics.Updated += OnDiagnosticsUpdated;
        }

        TransportOptions =
        [
            new("USB", "Wired connection through a USB cable.", Note(capabilities.Usb)),
            new("Wireless", "Authenticated connection over your local network.", Note(capabilities.Wireless)),
        ];
        ModeOptions =
        [
            new("Mirror", $"Show a {HostPlatform.Name} display on the Android device.", Note(capabilities.Mirror)),
            new("Extend", HostPlatform.IsWindows ? "Use the tablet as an extra display (needs the free Virtual Display Driver)." : "Use the tablet as an extra display, created while the tablet is connected.", Note(capabilities.Extend)),
        ];

        _session.StateChanged += OnSessionStateChanged;
    }

    /// <summary>Ordered like <see cref="TransportKind"/>, so an index converts directly.</summary>
    public IReadOnlyList<ConnectOption> TransportOptions { get; }

    /// <summary>Ordered like <see cref="DisplayMode"/>, so an index converts directly.</summary>
    public IReadOnlyList<ConnectOption> ModeOptions { get; }

    public TransportKind SelectedTransport
    {
        get => _selectedTransport;
        set
        {
            if (CanEditSelection && SetProperty(ref _selectedTransport, value))
            {
                if (value == TransportKind.Wireless)
                {
                    _wireless?.Browser?.Start();
                    RefreshWirelessDevices();
                }

                OnPropertyChanged(nameof(ShowsManualAddress));

                ShowDevicesFor(value);
            }
        }
    }

    public DisplayMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (CanEditSelection && SetProperty(ref _selectedMode, value))
            {
                if (value == DisplayMode.Extend)
                {
                    RefreshExtendStatus();
                }

                OnPropertyChanged(nameof(ShowsDisplayChoice));
                OnPropertyChanged(nameof(ShowsExtendStatus));
                OnSelectionChanged();
            }
        }
    }

    /// <summary>Whether this screen lists devices at all. False only for a view model built without discovery.</summary>
    public bool ShowsDevices => _discovery is not null || _wireless is not null;

    /// <summary>Whether the add-by-address box is shown: on Wi-Fi only.</summary>
    public bool ShowsManualAddress => _wireless is not null && SelectedTransport == TransportKind.Wireless;

    /// <summary>The attached Android devices as last listed, usable ones first.</summary>
    public IReadOnlyList<DeviceOption> Devices => _devices;

    public bool HasDevices => _devices.Count > 0;

    /// <summary>What the device list says, or why there is none: shown above or instead of the list.</summary>
    public string DevicesStatus => _devicesStatus;

    /// <summary>The device a session will use. Only one that is in <see cref="Devices"/> can be chosen.</summary>
    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!CanEditSelection || (value is not null && !_devices.Contains(value)))
            {
                return;
            }

            if (SetProperty(ref _selectedDevice, value))
            {
                OnSelectionChanged();
            }
        }
    }

    /// <summary>Whether this screen offers a choice of display. False without a catalogue.</summary>
    public bool ShowsDisplays => _displays is not null;

    /// <summary>The monitors attached when last listed, the main one first.</summary>
    public IReadOnlyList<DisplayOption> Displays => _displayOptions;

    /// <summary>The monitor a session will mirror; the main one unless the user chose another (D68).</summary>
    public DisplayOption? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (CanEditSelection && (value is null || _displayOptions.Contains(value)) && SetProperty(ref _selectedDisplay, value))
            {
                OnSelectionChanged();
            }
        }
    }

    /// <summary>One line about the stream while it runs (D67); null when there is nothing to say.</summary>
    public string? StreamHealth => Phase == SessionPhase.Streaming ? _streamHealth : null;

    /// <summary>Whether <see cref="StreamHealth"/> describes a problem.</summary>
    public bool StreamHealthIsProblem => Phase == SessionPhase.Streaming && _streamHealthIsProblem;

    public SessionPhase Phase => _session.State.Phase;

    /// <summary>Transport, mode and device are fixed once a session has been requested.</summary>
    public bool CanEditSelection => Phase == SessionPhase.Disconnected;

    public bool CanConnect => Phase == SessionPhase.Disconnected && ConnectBlockedReason is null && DeviceHint is null;

    public bool CanDisconnect => Phase is SessionPhase.Connecting
        or SessionPhase.Negotiating
        or SessionPhase.Streaming
        or SessionPhase.Reconnecting;

    public bool HasError => Phase == SessionPhase.Failed;

    /// <summary>Why the current selection cannot be started in this build; null when it can.</summary>
    public string? ConnectBlockedReason =>
        _capabilities.For(SelectedTransport).Reason
        ?? (SelectedTransport == TransportKind.Wireless && _wireless is null ? "Wireless is not set up in this window." : null)
        ?? _capabilities.For(SelectedMode).Reason
        ?? ExtendBlockedReason;

    // ---- Extend (D87–D91) ----------------------------------------------------------------------

    /// <summary>Whether the Mirror display picker is shown: Mirror only; Extend's display is the virtual one.</summary>
    public bool ShowsDisplayChoice => _displays is not null && SelectedMode == DisplayMode.Mirror;

    /// <summary>Whether the Extend status card is shown.</summary>
    public bool ShowsExtendStatus => SelectedMode == DisplayMode.Extend;

    /// <summary>The virtual display's state as last read (refreshed with the display list); null before.</summary>
    public ExtendStatus? ExtendStatus => _extendStatus;

    /// <summary>One line for the Extend card: ready, or what to do.</summary>
    public string ExtendStatusText => _virtualDisplays is null
        ? "Extend is not set up in this window."
        : _extendStatus?.Message ?? "Looking for the virtual display…";

    public bool ExtendIsReady => _extendStatus?.Readiness == ExtendReadiness.Ready;

    /// <summary>The Extend card shows a problem: Extend is chosen and not ready.</summary>
    public bool ExtendIsProblem => !ExtendIsReady;

    private string? ExtendBlockedReason => SelectedMode != DisplayMode.Extend
        ? null
        : _virtualDisplays is null
            ? "Extend is not set up in this window."
            : _extendStatus is { Readiness: ExtendReadiness.Ready } ? null : _extendStatus?.Message ?? "Looking for the virtual display…";

    /// <summary>Reads the display configuration again (read only, D88). Called with each display refresh.</summary>
    public void RefreshExtendStatus()
    {
        if (_virtualDisplays is null || !CanEditSelection)
        {
            return;
        }

        ExtendStatus status;
        try
        {
            // D98: a platform that makes its virtual display itself (Linux) reports its own status.
            status = _virtualDisplays is IExtendStatusSource source ? source.Status() : VirtualDisplayDetector.Status(_virtualDisplays.Targets());
        }
        catch (InvalidOperationException)
        {
            status = VirtualDisplayDetector.Status([]);
        }

        RefreshModeAdvice(status);
        if (Equals(status, _extendStatus))
        {
            return;
        }

        _extendStatus = status;
        OnPropertyChanged(nameof(ExtendStatus));
        OnPropertyChanged(nameof(ExtendStatusText));
        OnPropertyChanged(nameof(ExtendIsReady));
        OnPropertyChanged(nameof(ExtendIsProblem));
        OnPropertyChanged(nameof(CanBringWindowsBack));
        OnSelectionChanged();
    }

    /// <summary>The display-mode advice for the virtual display (D92); null until one is ready.</summary>
    public ModeAdvice? ModeAdvice => _modeAdvice;

    public bool ShowsModeAdvice => _modeAdvice?.Recommended is not null && ShowsExtendStatus;

    public string ModeAdviceText => string.Join(' ', new[] { _displayActionResult, _modeAdvice?.Message }.Where(text => !string.IsNullOrEmpty(text)));

    public bool CanApplyRecommendedMode => _modes is not null && CanEditSelection && _modeAdvice is { CurrentIsRecommended: false, Recommended: not null };

    public string ApplyRecommendedModeLabel => _modeAdvice?.Recommended is { } best ? $"Use {best.Mode}" : "Use the recommended mode";

    public bool CanBringWindowsBack => _rescue is not null && CanEditSelection && _extendStatus?.Readiness == ExtendReadiness.Ready;

    /// <summary>Changes the virtual display, and only it, to the recommended mode (D92). Between sessions only (ES-057).</summary>
    public void ApplyRecommendedMode()
    {
        if (!CanApplyRecommendedMode || _extendStatus?.Display?.Target.GdiDeviceName is not { } name)
        {
            return;
        }

        DisplayModeSpec mode = _modeAdvice!.Recommended!.Mode;
        string? problem = _modes!.Apply(name, mode);
        _displayActionResult = problem ?? $"The virtual display now uses {mode}.";
        _modeAdvice = null;
        RefreshDisplays();
        RefreshExtendStatus();
        OnPropertyChanged(nameof(ModeAdviceText));
    }

    /// <summary>Moves windows from the virtual display to the main display (ES-141); only when asked.</summary>
    public void BringWindowsBack()
    {
        if (!CanBringWindowsBack || _extendStatus?.Display?.Target.GdiDeviceName is not { } name)
        {
            return;
        }

        int moved = _rescue!.BringBack(name);
        _displayActionResult = moved switch
        {
            0 => "No windows were on the virtual display.",
            1 => "Moved 1 window to the main display.",
            _ => $"Moved {moved} windows to the main display.",
        };
        OnPropertyChanged(nameof(ModeAdviceText));
    }

    private void RefreshModeAdvice(ExtendStatus status)
    {
        ModeAdvice? advice = null;
        if (_modes is not null && status is { Readiness: ExtendReadiness.Ready, Display.Target.GdiDeviceName: { } name })
        {
            try
            {
                ClientCapabilities? client = _lastClient();
                TabletScreen? tablet = client is { DisplayWidth: > 0, DisplayHeight: > 0 } ? new TabletScreen(client.DisplayWidth, client.DisplayHeight, client.RefreshHz) : null;
                advice = TabletModeAdvisor.Advise(_modes.Available(name), _modes.Current(name), tablet);
            }
            catch (InvalidOperationException)
            {
                advice = null;
            }
        }

        if (Equals(advice?.Message, _modeAdvice?.Message) && Equals(advice?.CurrentIsRecommended, _modeAdvice?.CurrentIsRecommended))
        {
            _modeAdvice = advice;
            return;
        }

        _modeAdvice = advice;
        OnPropertyChanged(nameof(ModeAdvice));
        OnPropertyChanged(nameof(ShowsModeAdvice));
        OnPropertyChanged(nameof(ModeAdviceText));
        OnPropertyChanged(nameof(CanApplyRecommendedMode));
        OnPropertyChanged(nameof(ApplyRecommendedModeLabel));
        OnPropertyChanged(nameof(CanBringWindowsBack));
    }

    public bool IsConnectBlocked => ConnectBlockedReason is not null;

    /// <summary>What the user has to do about the device before Connect is possible; null when nothing.</summary>
    public string? DeviceHint
    {
        get
        {
            if (SelectedTransport == TransportKind.Wireless && _wireless is not null)
            {
                if (ShowsDisplayChoice && _selectedDisplay is null)
                {
                    return _displayOptions.Count == 0 ? $"{HostPlatform.Name} reports no display to mirror." : "Choose the display to mirror.";
                }

                return _selectedDevice?.WirelessDevice switch
                {
                    null when _devices.Count == 0 => _devicesStatus,
                    null => "Choose the tablet to connect to.",
                    { IsCompatible: false } => $"{_selectedDevice.Title} uses an incompatible version of ExtendSpace. Update both apps.",
                    { IsPaired: false } => $"Pair with {_selectedDevice.Title} first: on the tablet tap Pair with a PC, then choose Pair here.",
                    { IsAvailable: false } => $"{_selectedDevice.Title} is not on the network. On the tablet, turn on Wi-Fi connections and tap Wait for PC.",
                    _ => null,
                };
            }

            if (_discovery is null || SelectedTransport != TransportKind.Usb)
            {
                return ShowsDisplayChoice && _selectedDisplay is null && _displayOptions.Count == 0 ? $"{HostPlatform.Name} reports no display to mirror." : null;
            }

            if (ShowsDisplayChoice && _selectedDisplay is null)
            {
                return _displayOptions.Count == 0 ? $"{HostPlatform.Name} reports no display to mirror." : "Choose the display to mirror.";
            }

            return _selectedDevice switch
            {
                { IsUsable: true } => null,
                { } notReady => $"{notReady.Title}: {notReady.Status}.",
                null when !_devicesListed => _devicesStatus,
                null when _devices.Any(device => device.IsUsable) => "Choose the device to connect to.",
                null => _devicesStatus,
            };
        }
    }

    public string StatusTitle => Phase switch
    {
        SessionPhase.Disconnected => "Disconnected",
        SessionPhase.Connecting => "Connecting",
        SessionPhase.Negotiating => "Negotiating",
        SessionPhase.Streaming => "Connected",
        SessionPhase.Reconnecting => "Reconnecting",
        SessionPhase.Disconnecting => "Disconnecting",
        SessionPhase.Failed => "Connection failed",
        _ => throw new InvalidOperationException($"Unknown phase {Phase}."),
    };

    public string StatusDetail
    {
        get
        {
            SessionState state = _session.State;
            return state switch
            {
                { Phase: SessionPhase.Disconnected } => "No Android device is connected.",
                { Phase: SessionPhase.Failed, Error: { } error } => Describe(error),
                { Phase: SessionPhase.Connecting } => "Waiting for ExtendSpace on the device. Open the app on the device and tap Wait for PC.",
                { Phase: SessionPhase.Reconnecting, Request.Transport: TransportKind.Wireless } => "The connection was interrupted. Trying again; check that the tablet is on the same Wi-Fi and the app is open on it.",
                { Phase: SessionPhase.Reconnecting } => "The connection was interrupted. Trying again; check the cable and that the app is open on the device.",
                { Request: { } request } => $"{Describe(request.Mode)} over {Describe(request.Transport)}.",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Asks for a session with the current selection. Does nothing unless <see cref="CanConnect"/>.</summary>
    public bool Connect()
    {
        if (!CanConnect)
        {
            return false;
        }

        // The main display is asked for as such, so that a session follows it if Windows renumbers
        // displays; another is asked for by name and never replaced by a different one (D68). In
        // Extend the source is the virtual monitor, by name, so a physical one is never captured (D89).
        string? monitor = SelectedMode == DisplayMode.Extend
            ? _extendStatus?.Display?.Target.GdiDeviceName
            : _selectedDisplay is { IsPrimary: false } chosen ? chosen.DeviceName : null;
        if (SelectedMode == DisplayMode.Extend && monitor is null)
        {
            return false;
        }

        if (_selection is not null)
        {
            _selection.Transport = SelectedTransport;
            _selection.Serial = SelectedTransport == TransportKind.Usb ? _selectedDevice?.Serial : null;
            _selection.WirelessDeviceId = SelectedTransport == TransportKind.Wireless ? _selectedDevice?.WirelessDevice?.DeviceId : null;
            _selection.MonitorDeviceName = monitor;
            _selection.Mode = SelectedMode;
        }

        return _session.Start(new SessionRequest(SelectedTransport, SelectedMode, MonitorDeviceName: monitor)) == SessionStartResult.Started;
    }

    public bool Disconnect() => _session.Stop();

    public bool AcknowledgeError() => _session.AcknowledgeError();

    /// <summary>
    /// Lists the devices again. Called by the page when it is shown and periodically while a device
    /// can be chosen. A call while one is running does nothing. Never throws for what adb does.
    /// Continues on the caller's synchronization context, so call it from the UI thread.
    /// </summary>
    public async Task RefreshDevicesAsync(CancellationToken cancellation = default)
    {
        if (_discovery is null || Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            DeviceListResult result;
            try
            {
                result = await _discovery.ListAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Apply(result);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Lists the monitors again (D68): quick, so the page calls it with each device refresh. The choice
    /// is kept by device name; a chosen display that has gone falls back to the main one, which is
    /// what an unconfigured session mirrors. Fixed while a session runs.
    /// </summary>
    public void RefreshDisplays()
    {
        if (_displays is null || !CanEditSelection)
        {
            return;
        }

        IReadOnlyList<DisplayInfo> listed;
        try
        {
            listed = _displays.List();
        }
        catch (InvalidOperationException)
        {
            listed = [];
        }

        RefreshExtendStatus();

        // A virtual display is Extend's, not something to mirror (D89).
        HashSet<string> virtualNames = _virtualDisplays is null
            ? []
            : [.. SafeTargets().Where(target => VirtualDisplayDetector.Classify(target) is not null && target.GdiDeviceName is not null).Select(target => target.GdiDeviceName!)];
        IReadOnlyList<DisplayOption> options = [.. listed
            .Where(display => !virtualNames.Contains(display.DeviceName))
            .Select(DisplayOption.From)
            .OrderByDescending(option => option.IsPrimary)
            .ThenBy(option => option.Title, StringComparer.CurrentCulture)];
        DisplayOption? selected = (_selectedDisplay is { } previous ? options.FirstOrDefault(option => option.DeviceName == previous.DeviceName) : null)
            ?? options.FirstOrDefault(option => option.IsPrimary)
            ?? options.FirstOrDefault();
        if (options.SequenceEqual(_displayOptions) && Equals(selected, _selectedDisplay))
        {
            return;
        }

        _displayOptions = options;
        _selectedDisplay = selected;
        OnPropertyChanged(nameof(Displays));
        OnPropertyChanged(nameof(SelectedDisplay));
        OnSelectionChanged();
    }

    private void OnDiagnosticsUpdated(object? sender, EventArgs e)
    {
        StreamHealthReport? health = _diagnostics?.Latest?.Health;
        string? text = health is null ? null : StreamDiagnosticsText.Summary(health);
        bool problem = health is not null && StreamDiagnosticsText.IsProblem(health.State);
        void Publish()
        {
            if (text != _streamHealth || problem != _streamHealthIsProblem)
            {
                _streamHealth = text;
                _streamHealthIsProblem = problem;
                OnPropertyChanged(nameof(StreamHealth));
                OnPropertyChanged(nameof(StreamHealthIsProblem));
            }
        }

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Publish();
        }
        else
        {
            _uiContext.Post(_ => Publish(), null);
        }
    }

    private void Apply(DeviceListResult result)
    {
        IReadOnlyList<DeviceOption> devices = [.. result.Devices
            .Select(DeviceOption.From)
            .OrderByDescending(device => device.IsUsable)
            .ThenBy(device => device.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Serial, StringComparer.Ordinal)];

        string status = result.Status switch
        {
            DeviceListStatus.Listed when devices.Count == 0 =>
                "No Android device found. Connect one with a USB cable and turn on USB debugging in its developer options.",
            DeviceListStatus.Listed when !devices.Any(device => device.IsUsable) =>
                "No device is ready yet.",
            DeviceListStatus.Listed => devices.Count == 1 ? "1 device found." : $"{devices.Count} devices found.",
            _ => result.Problem ?? "The devices could not be listed.",
        };

        // The choice is kept by serial, so that a refresh never moves it to another device; it is
        // made automatically only when exactly one usable device is attached.
        DeviceOption? selected = _selectedDevice is { } previous
            ? devices.FirstOrDefault(device => device.Serial == previous.Serial)
            : null;
        if (selected is null && devices.Count(device => device.IsUsable) == 1)
        {
            selected = devices.First(device => device.IsUsable);
        }

        _usbDevices = devices;
        _usbStatus = status;
        if (SelectedTransport != TransportKind.Usb)
        {
            // Kept for when USB is chosen again; the wireless list is the one shown.
            _devicesListed = true;
            return;
        }

        bool changed = !_devices.SequenceEqual(devices) || _devicesStatus != status || !_devicesListed;
        _devices = devices;
        _devicesStatus = status;
        _devicesListed = true;

        // While a session runs the choice is fixed; the list is still shown as it is.
        if (CanEditSelection)
        {
            _selectedDevice = selected;
        }

        if (changed || CanEditSelection)
        {
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(DevicesStatus));
            OnPropertyChanged(nameof(SelectedDevice));
            OnSelectionChanged();
        }
    }

    // ---- Input (D81–D84) ---------------------------------------------------------------------

    /// <summary>Whether this screen offers the input choice. False without an input path.</summary>
    public bool ShowsInput => _input is not null;

    /// <summary>Ordered like <see cref="Input.InputMode"/>, so an index converts directly.</summary>
    public IReadOnlyList<ConnectOption> InputOptions { get; }

    /// <summary>
    /// Off by default. Can be changed at any time: Off releases every touch at once; Touch or Mouse is
    /// offered to the tablet when the next connection starts (it takes effect at once if the current
    /// connection was already offered input).
    /// </summary>
    public Input.InputMode SelectedInputMode
    {
        get => _input?.Mode ?? Input.InputMode.Off;
        set
        {
            if (_input is not null && _input.Mode != value && _inputAvailability?.Unavailable(value) is { } reason)
            {
                // Refused: the list goes back to the mode in force, and the option says why.
                _inputRefusal = reason;
                OnPropertyChanged(nameof(SelectedInputMode));
                OnPropertyChanged(nameof(InputNote));
                return;
            }

            _inputRefusal = null;
            if (_input is not null && _input.Mode != value)
            {
                _input.Mode = value;
                OnPropertyChanged(nameof(SelectedInputMode));
                OnPropertyChanged(nameof(InputNote));
            }
        }
    }

    private string? _inputRefusal;

    /// <summary>What the user should know about the input choice now; null when nothing.</summary>
    public string? InputNote => _inputRefusal ?? (Phase, SelectedInputMode) switch
    {
        (_, Input.InputMode.Off) => null,
        (SessionPhase.Streaming, _) => "Applies to this connection if it started with input on; otherwise from the next connection.",
        _ when _inputAvailability?.Note is { } note => note,
        _ => HostPlatform.IsWindows
            ? "Windows that run as administrator, and the sign-in and UAC screens, ignore input from the tablet."
            : "Your desktop may ask once to allow remote control. On an X11 session the tablet works as a mouse only.",
    };

    // ---- Wireless (D74–D79) -------------------------------------------------------------------

    /// <summary>What pairing is doing, or how it ended; null when nothing has been tried.</summary>
    public string? PairingStatus => _pairingStatus;

    /// <summary>The chosen tablet can be paired: on Wi-Fi, not paired, found, and no session or pairing running.</summary>
    public bool CanPair => _wireless is not null && !_pairing && CanEditSelection
        && SelectedTransport == TransportKind.Wireless && _selectedDevice?.WirelessDevice is { IsPaired: false, IsAvailable: true, IsCompatible: true };

    /// <summary>The chosen tablet is paired and can be forgotten.</summary>
    public bool CanForget => _wireless is not null && !_pairing && CanEditSelection
        && SelectedTransport == TransportKind.Wireless && _selectedDevice?.WirelessDevice is { IsPaired: true, DeviceId: not null };

    /// <summary>
    /// Pairs with the chosen tablet. <paramref name="confirm"/> shows the six digits and returns the
    /// user's answer. Continues on the caller's synchronization context.
    /// </summary>
    public async Task<PairingResult?> PairSelectedAsync(PairingConfirmation confirm, CancellationToken cancellation = default)
    {
        if (!CanPair || _selectedDevice?.WirelessDevice is not { } device)
        {
            return null;
        }

        _pairing = true;
        SetPairingStatus($"Connecting to {device.Name}…");
        try
        {
            PairingResult result = await _wireless!.PairAsync(device, confirm, cancellation);
            SetPairingStatus(result.Message);
            return result;
        }
        finally
        {
            _pairing = false;
            RefreshWirelessDevices();
            OnPairingChanged();
        }
    }

    /// <summary>What went wrong with the last address typed in; null when nothing.</summary>
    public string? ManualAddressError { get; private set; }

    /// <summary>
    /// Adds a tablet by its IP address (and optional port), for networks where it is not found by
    /// itself (ES-126), and chooses it. False, with <see cref="ManualAddressError"/> set, if the text is
    /// not an address.
    /// </summary>
    public bool AddWirelessAddress(string? text)
    {
        if (_wireless is null || !CanEditSelection)
        {
            return false;
        }

        System.Net.IPEndPoint? endPoint = WirelessDirectory.ParseAddress(text);
        ManualAddressError = endPoint is null ? "Type the tablet's IP address as shown on it, for example 192.168.1.20." : null;
        OnPropertyChanged(nameof(ManualAddressError));
        if (endPoint is null)
        {
            return false;
        }

        string key = _wireless.Directory.AddManual(endPoint);
        RefreshWirelessDevices();
        SelectedDevice = _devices.FirstOrDefault(device => device.Serial == key);
        return true;
    }

    /// <summary>Forgets the chosen paired tablet: it can no longer be connected to until paired again.</summary>
    public bool ForgetSelected()
    {
        if (!CanForget || _selectedDevice?.WirelessDevice?.DeviceId is not { } id)
        {
            return false;
        }

        string name = _selectedDevice.Title;
        bool removed = _wireless!.Forget(id);
        SetPairingStatus(removed ? $"Forgot {name}. Pair again to connect to it over Wi-Fi." : null);
        RefreshWirelessDevices();
        return removed;
    }

    /// <summary>Reads the wireless list again from discovery and the trust list. Called on the UI thread.</summary>
    public void RefreshWirelessDevices()
    {
        if (_wireless is null)
        {
            return;
        }

        _wirelessDevices = [.. _wireless.Directory.Devices
            .Select(DeviceOption.From)
            .OrderByDescending(device => device.IsUsable)
            .ThenByDescending(device => device.WirelessDevice!.IsPaired)
            .ThenBy(device => device.Title, StringComparer.CurrentCultureIgnoreCase)];
        if (SelectedTransport == TransportKind.Wireless)
        {
            ShowDevicesFor(TransportKind.Wireless);
        }
    }

    /// <summary>Puts the chosen route's list on screen, keeping the choice by key, or choosing the only usable one.</summary>
    private void ShowDevicesFor(TransportKind transport)
    {
        IReadOnlyList<DeviceOption> devices = transport == TransportKind.Wireless ? _wirelessDevices : _usbDevices;
        string status = transport == TransportKind.Wireless
            ? devices.Count == 0
                ? "No tablet found on this network. On the tablet, turn on Wi-Fi connections and tap Wait for PC (or Pair with a PC)."
                : devices.Count == 1 ? "1 tablet found." : $"{devices.Count} tablets found."
            : _usbStatus;

        DeviceOption? selected = _selectedDevice is { } previous ? devices.FirstOrDefault(device => device.Serial == previous.Serial) : null;
        if (selected is null && devices.Count(device => device.IsUsable) == 1)
        {
            selected = devices.First(device => device.IsUsable);
        }

        _devices = devices;
        _devicesStatus = status;
        if (CanEditSelection)
        {
            _selectedDevice = selected;
        }

        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(DevicesStatus));
        OnPropertyChanged(nameof(SelectedDevice));
        OnSelectionChanged();
    }

    private void SetPairingStatus(string? status)
    {
        _pairingStatus = status;
        OnPairingChanged();
    }

    private void OnPairingChanged()
    {
        OnPropertyChanged(nameof(PairingStatus));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(CanForget));
    }

    private void OnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private IReadOnlyList<DisplayTarget> SafeTargets()
    {
        try
        {
            return _virtualDisplays?.Targets() ?? [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static string Note(Availability availability) => availability.Reason ?? string.Empty;

    private static string Describe(DisplayMode mode) => mode switch
    {
        DisplayMode.Mirror => "Mirroring",
        DisplayMode.Extend => "Extending",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string Describe(TransportKind transport) => transport switch
    {
        TransportKind.Usb => "USB",
        TransportKind.Wireless => "the local network",
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    private static string Describe(SessionError error)
    {
        string summary = error.Code switch
        {
            SessionErrorCode.TransportUnavailable => "The connection to the device could not be opened.",
            SessionErrorCode.AuthenticationFailed => "The device could not be authenticated.",
            SessionErrorCode.IncompatibleProtocol => "The device uses an incompatible protocol version.",
            SessionErrorCode.NegotiationFailed => "The host and the device could not agree on stream settings.",
            SessionErrorCode.ConnectionLost => "The connection to the device was lost.",
            SessionErrorCode.CaptureUnavailable => "The display could not be captured. If it was disconnected or changed resolution, start again.",
            SessionErrorCode.EncoderUnavailable => "No video encoder on this PC could be used for the stream.",
            SessionErrorCode.Internal => "An unexpected error occurred.",
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };

        string? detail = Advice(error.Cause?.Code) ?? error.Detail;
        return detail is null ? summary : $"{summary} {detail}";
    }

    /// <summary>
    /// What to do, for the failures a user can fix themselves, in place of the component's own
    /// technical sentence (Phase 6 error review). Null keeps the component's detail.
    /// </summary>
    internal static string? Advice(string? faultCode) => faultCode switch
    {
        "no_app_listening" => "ExtendSpace is not waiting on the device. Open ExtendSpace on the device, tap Wait for PC, then connect again.",
        "adb_not_found" => "adb was not found. Install the Android SDK Platform-Tools and set ANDROID_HOME, or put adb on PATH (see the installation guide).",
        "adb_not_startable" => "adb was found but could not be started. Reinstall the Android SDK Platform-Tools.",
        "device_unauthorized" => "Unlock the device and allow USB debugging for this PC when it asks, then connect again.",
        "device_not_attached" => "The device is not connected, or USB debugging is off. Check the cable and USB debugging, then connect again.",
        "several_devices_attached" => "More than one Android device is connected. Choose the device in the list.",
        "keepalive_timeout" or "send_timeout" => "The device stopped answering. Check that ExtendSpace is open on the device and the cable is connected.",
        "not_paired" => "The tablet does not recognise this PC. Pair them again: on the tablet tap Pair with a PC, then choose Pair here.",
        "authentication_failed" => "The tablet's identity has changed (for example, ExtendSpace was reinstalled on it). Forget it here, then pair again.",
        "wireless_device_not_found" => "The tablet was not found on the network. Check that both are on the same Wi-Fi, and that the tablet has Wi-Fi connections on and is waiting for the PC.",
        _ => null,
    };

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(ConnectBlockedReason));
        OnPropertyChanged(nameof(IsConnectBlocked));
        OnPropertyChanged(nameof(DeviceHint));
        OnPropertyChanged(nameof(CanConnect));
    }

    // The coordinator will dispatch from background threads; bindings must only be touched on the UI thread.
    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            OnPropertyChanged(string.Empty);
        }
        else
        {
            _uiContext.Post(_ => OnPropertyChanged(string.Empty), null);
        }
    }
}
