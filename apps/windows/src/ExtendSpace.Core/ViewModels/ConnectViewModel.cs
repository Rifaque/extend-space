using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.ViewModels;

/// <summary>One selectable transport or display mode as shown on the Connect screen.</summary>
/// <param name="StatusNote">Why the option cannot be used in this build, or empty when it can.</param>
public sealed record ConnectOption(string Title, string Description, string StatusNote)
{
    // List controls fall back to ToString() for the accessible name of a templated item.
    public override string ToString() => Title;
}

/// <summary>State and intent for the Connect screen. Holds no transport or streaming logic.</summary>
public sealed class ConnectViewModel : ObservableObject
{
    private readonly SessionStateMachine _session;
    private readonly SessionCapabilities _capabilities;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private TransportKind _selectedTransport = TransportKind.Usb;
    private DisplayMode _selectedMode = DisplayMode.Mirror;

    public ConnectViewModel(SessionStateMachine session, SessionCapabilities capabilities)
    {
        _session = session;
        _capabilities = capabilities;

        TransportOptions =
        [
            new("USB", "Wired connection through a USB cable.", Note(capabilities.Usb)),
            new("Wireless", "Authenticated connection over your local network.", Note(capabilities.Wireless)),
        ];
        ModeOptions =
        [
            new("Mirror", "Show an existing Windows display on the Android device.", Note(capabilities.Mirror)),
            new("Extend", "Add the Android device to Windows as a separate display.", Note(capabilities.Extend)),
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
                OnSelectionChanged();
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
                OnSelectionChanged();
            }
        }
    }

    public SessionPhase Phase => _session.State.Phase;

    /// <summary>Transport and mode are fixed once a session has been requested.</summary>
    public bool CanEditSelection => Phase == SessionPhase.Disconnected;

    public bool CanConnect => Phase == SessionPhase.Disconnected && ConnectBlockedReason is null;

    public bool CanDisconnect => Phase is SessionPhase.Connecting
        or SessionPhase.Negotiating
        or SessionPhase.Streaming
        or SessionPhase.Reconnecting;

    public bool HasError => Phase == SessionPhase.Failed;

    /// <summary>Why the current selection cannot be started in this build; null when it can.</summary>
    public string? ConnectBlockedReason =>
        _capabilities.For(SelectedTransport).Reason ?? _capabilities.For(SelectedMode).Reason;

    public bool IsConnectBlocked => ConnectBlockedReason is not null;

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
                { Request: { } request } => $"{Describe(request.Mode)} over {Describe(request.Transport)}.",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Asks for a session with the current selection. Does nothing unless <see cref="CanConnect"/>.</summary>
    public bool Connect() =>
        CanConnect && _session.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(SelectedTransport, SelectedMode)));

    public bool Disconnect() => _session.Dispatch(new SessionEvent.DisconnectRequested());

    public bool AcknowledgeError() => _session.Dispatch(new SessionEvent.ErrorAcknowledged());

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
            SessionErrorCode.Internal => "An unexpected error occurred.",
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };

        return error.Detail is null ? summary : $"{summary} {error.Detail}";
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(ConnectBlockedReason));
        OnPropertyChanged(nameof(IsConnectBlocked));
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
