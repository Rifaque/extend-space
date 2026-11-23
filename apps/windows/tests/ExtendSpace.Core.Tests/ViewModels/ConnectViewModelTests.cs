using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.ViewModels;

public class ConnectViewModelTests
{
    private static readonly SessionCapabilities EverythingWorks = new(
        Availability.Available,
        Availability.Available,
        Availability.Available,
        Availability.Available);

    private static readonly SessionCapabilities MirrorOverUsbOnly = EverythingWorks with
    {
        Wireless = Availability.Unavailable("no wireless"),
        Extend = Availability.Unavailable("no driver"),
    };

    [Fact]
    public void The_current_build_connects_only_usb_mirror_and_says_why_the_rest_cannot()
    {
        // D65: USB Mirror is enabled; wireless and Extend are not built.
        foreach (TransportKind transport in Enum.GetValues<TransportKind>())
        {
            foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
            {
                var session = new SessionStateMachine();
                var viewModel = new ConnectViewModel(new MachineSessionControl(session), SessionCapabilities.CurrentBuild)
                {
                    SelectedTransport = transport,
                    SelectedMode = mode,
                };

                bool usbMirror = transport == TransportKind.Usb && mode == DisplayMode.Mirror;
                Assert.Equal(usbMirror, viewModel.CanConnect);
                Assert.Equal(!usbMirror, viewModel.IsConnectBlocked);
                Assert.Equal(usbMirror, string.IsNullOrWhiteSpace(viewModel.ConnectBlockedReason));
                Assert.Equal(usbMirror, viewModel.Connect());
                Assert.Equal(usbMirror ? SessionPhase.Connecting : SessionPhase.Disconnected, session.State.Phase);
            }
        }
    }

    [Fact]
    public void Options_are_ordered_like_their_enums_and_carry_the_unavailable_reason()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(new SessionStateMachine()), MirrorOverUsbOnly);

        Assert.Equal(["USB", "Wireless"], viewModel.TransportOptions.Select(option => option.Title));
        Assert.Equal(["Mirror", "Extend"], viewModel.ModeOptions.Select(option => option.Title));
        Assert.Equal(string.Empty, viewModel.TransportOptions[(int)TransportKind.Usb].StatusNote);
        Assert.Equal("no wireless", viewModel.TransportOptions[(int)TransportKind.Wireless].StatusNote);
        Assert.Equal("no driver", viewModel.ModeOptions[(int)DisplayMode.Extend].StatusNote);
    }

    [Fact]
    public void Blocked_reason_follows_the_selection()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(new SessionStateMachine()), MirrorOverUsbOnly);
        Assert.True(viewModel.CanConnect);

        viewModel.SelectedMode = DisplayMode.Extend;
        Assert.False(viewModel.CanConnect);
        Assert.Equal("no driver", viewModel.ConnectBlockedReason);

        viewModel.SelectedTransport = TransportKind.Wireless;
        Assert.Equal("no wireless", viewModel.ConnectBlockedReason);

        viewModel.SelectedTransport = TransportKind.Usb;
        viewModel.SelectedMode = DisplayMode.Mirror;
        Assert.Null(viewModel.ConnectBlockedReason);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public void Connect_requests_a_session_with_the_selection_and_locks_it()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(session), EverythingWorks)
        {
            // Wireless needs a paired tablet since D74 (WirelessViewModelTests) and Extend a ready
            // virtual display since D89 (ExtendViewModelTests); USB Mirror with no device list needs
            // nothing, so it still shows that the selection is requested and then locked.
            SelectedTransport = TransportKind.Usb,
            SelectedMode = DisplayMode.Mirror,
        };

        Assert.True(viewModel.Connect());

        Assert.Equal(
            new SessionState(SessionPhase.Connecting, new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)),
            session.State);
        Assert.False(viewModel.CanConnect);
        Assert.True(viewModel.CanDisconnect);
        Assert.False(viewModel.CanEditSelection);

        viewModel.SelectedTransport = TransportKind.Wireless;
        Assert.Equal(TransportKind.Usb, viewModel.SelectedTransport);
    }

    [Fact]
    public void Status_text_describes_the_session_in_progress()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(session), EverythingWorks);

        viewModel.Connect();
        session.Dispatch(new SessionEvent.TransportConnected());
        session.Dispatch(new SessionEvent.NegotiationCompleted());

        Assert.Equal("Connected", viewModel.StatusTitle);
        Assert.Equal("Mirroring over USB.", viewModel.StatusDetail);
    }

    [Fact]
    public void A_failure_is_shown_with_its_detail_until_acknowledged()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(session), EverythingWorks);

        viewModel.Connect();
        session.Dispatch(new SessionEvent.Failed(new SessionError(SessionErrorCode.TransportUnavailable, "adb was not found.")));

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.CanConnect);
        Assert.False(viewModel.CanDisconnect);
        Assert.Equal("Connection failed", viewModel.StatusTitle);
        Assert.Equal("The connection to the device could not be opened. adb was not found.", viewModel.StatusDetail);

        Assert.True(viewModel.AcknowledgeError());
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public void Session_changes_made_elsewhere_refresh_every_binding()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(session), EverythingWorks);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));

        Assert.Equal([string.Empty], changed);
        Assert.Equal("Connecting", viewModel.StatusTitle);
    }

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Every_error_code_has_a_sentence(SessionErrorCode code)
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(session), EverythingWorks);

        viewModel.Connect();
        session.Dispatch(new SessionEvent.Failed(new SessionError(code)));

        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusDetail));
    }

    public static TheoryData<SessionErrorCode> ErrorCodes => [.. Enum.GetValues<SessionErrorCode>()];

    [Fact]
    public void Disconnect_is_rejected_when_nothing_is_in_progress()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(new SessionStateMachine()), EverythingWorks);

        Assert.False(viewModel.CanDisconnect);
        Assert.False(viewModel.Disconnect());
    }
}

public class ConnectViewModelCompositionTests
{
    [Fact]
    public void With_the_current_build_the_view_model_starts_the_session_owner_which_alone_decides_what_happens()
    {
        var coordinator = new SessionCoordinator(new SessionStateMachine(), new UnavailableSessionConnection(), new UnavailableVideoPipeline(), new SystemSessionClock());
        var viewModel = new ConnectViewModel(coordinator, SessionCapabilities.CurrentBuild);

        Assert.True(viewModel.Connect());

        // The stand-in pipeline fails the session at once, with a reason the screen can show.
        Assert.Equal(SessionPhase.Failed, coordinator.State.Phase);
        Assert.True(viewModel.HasError);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusDetail));
        Assert.True(viewModel.AcknowledgeError());
        Assert.Equal(SessionState.Disconnected, coordinator.State);
    }

    [Fact]
    public void Were_connect_enabled_the_stand_in_components_would_fail_the_session_at_once_with_a_reason()
    {
        var coordinator = new SessionCoordinator(new SessionStateMachine(), new UnavailableSessionConnection(), new UnavailableVideoPipeline(), new SystemSessionClock());

        Assert.Equal(SessionStartResult.Started, coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));

        Assert.Equal(SessionPhase.Failed, coordinator.State.Phase);
        Assert.Equal(SessionErrorCode.CaptureUnavailable, coordinator.State.Error!.Code);
        Assert.Equal("capture_not_built", coordinator.State.Error.Cause!.Code);
    }

    [Fact]
    public void As_composed_for_the_host_a_session_fails_at_the_stand_in_pipeline_before_adb_or_a_device_is_reached()
    {
        var adb = new RecordingAdb();
        SessionCoordinator coordinator = HostComposition.CreateSession("1.0.0", "852ec620", adb);

        Assert.Equal(SessionStartResult.Started, coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));

        Assert.Equal(SessionPhase.Failed, coordinator.State.Phase);
        Assert.Equal("capture_not_built", coordinator.State.Error!.Cause!.Code);
        Assert.Equal(0, adb.Commands);
        Assert.True(SessionCapabilities.CurrentBuild.Usb.IsAvailable);
        Assert.True(SessionCapabilities.CurrentBuild.Mirror.IsAvailable);
        Assert.True(SessionCapabilities.CurrentBuild.Wireless.IsAvailable);
        // Extend's readiness is decided at run time from the display configuration (D89).
        Assert.True(SessionCapabilities.CurrentBuild.Extend.IsAvailable);
    }

    [Fact]
    public void A_start_the_coordinator_refuses_is_not_reported_as_connected()
    {
        var control = new MachineSessionControl(new SessionStateMachine()) { Refuse = SessionStartResult.PreviousSessionNotReleased };
        var viewModel = new ConnectViewModel(control, new SessionCapabilities(Availability.Available, Availability.Available, Availability.Available, Availability.Available));

        Assert.False(viewModel.Connect());
        Assert.Equal("Disconnected", viewModel.StatusTitle);
    }
}

internal sealed class RecordingAdb : ExtendSpace.Core.Transport.IAdbCommandRunner
{
    private int _commands;

    public int Commands => Volatile.Read(ref _commands);

    public Task<ExtendSpace.Core.Transport.AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _commands);
        return Task.FromResult(new ExtendSpace.Core.Transport.AdbCommandResult(ExtendSpace.Core.Transport.AdbCommandStatus.NotFound));
    }
}

/// <summary>The session's owner reduced to its state machine, so that these tests can set any state directly.</summary>
internal sealed class MachineSessionControl(SessionStateMachine machine) : ISessionControl
{
    public SessionStartResult? Refuse { get; init; }

    public SessionState State => machine.State;

    public event EventHandler? StateChanged
    {
        add => machine.StateChanged += value;
        remove => machine.StateChanged -= value;
    }

    public SessionStartResult Start(SessionRequest request) =>
        Refuse ?? (machine.Dispatch(new SessionEvent.ConnectRequested(request)) ? SessionStartResult.Started : SessionStartResult.AlreadyActive);

    public bool Stop() => machine.Dispatch(new SessionEvent.DisconnectRequested());

    public bool AcknowledgeError() => machine.Dispatch(new SessionEvent.ErrorAcknowledged());
}
