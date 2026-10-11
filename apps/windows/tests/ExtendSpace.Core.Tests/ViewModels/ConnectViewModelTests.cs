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
    public void The_current_build_cannot_connect_with_any_selection_and_says_why()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(session, SessionCapabilities.CurrentBuild);

        foreach (TransportKind transport in Enum.GetValues<TransportKind>())
        {
            foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
            {
                viewModel.SelectedTransport = transport;
                viewModel.SelectedMode = mode;

                Assert.False(viewModel.CanConnect);
                Assert.True(viewModel.IsConnectBlocked);
                Assert.False(string.IsNullOrWhiteSpace(viewModel.ConnectBlockedReason));
                Assert.False(viewModel.Connect());
            }
        }

        Assert.Equal(SessionState.Disconnected, session.State);
        Assert.Equal("Disconnected", viewModel.StatusTitle);
    }

    [Fact]
    public void Options_are_ordered_like_their_enums_and_carry_the_unavailable_reason()
    {
        var viewModel = new ConnectViewModel(new SessionStateMachine(), MirrorOverUsbOnly);

        Assert.Equal(["USB", "Wireless"], viewModel.TransportOptions.Select(option => option.Title));
        Assert.Equal(["Mirror", "Extend"], viewModel.ModeOptions.Select(option => option.Title));
        Assert.Equal(string.Empty, viewModel.TransportOptions[(int)TransportKind.Usb].StatusNote);
        Assert.Equal("no wireless", viewModel.TransportOptions[(int)TransportKind.Wireless].StatusNote);
        Assert.Equal("no driver", viewModel.ModeOptions[(int)DisplayMode.Extend].StatusNote);
    }

    [Fact]
    public void Blocked_reason_follows_the_selection()
    {
        var viewModel = new ConnectViewModel(new SessionStateMachine(), MirrorOverUsbOnly);
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
        var viewModel = new ConnectViewModel(session, EverythingWorks)
        {
            SelectedTransport = TransportKind.Wireless,
            SelectedMode = DisplayMode.Extend,
        };

        Assert.True(viewModel.Connect());

        Assert.Equal(
            new SessionState(SessionPhase.Connecting, new SessionRequest(TransportKind.Wireless, DisplayMode.Extend)),
            session.State);
        Assert.False(viewModel.CanConnect);
        Assert.True(viewModel.CanDisconnect);
        Assert.False(viewModel.CanEditSelection);

        viewModel.SelectedTransport = TransportKind.Usb;
        Assert.Equal(TransportKind.Wireless, viewModel.SelectedTransport);
    }

    [Fact]
    public void Status_text_describes_the_session_in_progress()
    {
        var session = new SessionStateMachine();
        var viewModel = new ConnectViewModel(session, EverythingWorks);

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
        var viewModel = new ConnectViewModel(session, EverythingWorks);

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
        var viewModel = new ConnectViewModel(session, EverythingWorks);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));

        Assert.Equal([string.Empty], changed);
        Assert.Equal("Connecting", viewModel.StatusTitle);
    }

    [Fact]
    public void Disconnect_is_rejected_when_nothing_is_in_progress()
    {
        var viewModel = new ConnectViewModel(new SessionStateMachine(), EverythingWorks);

        Assert.False(viewModel.CanDisconnect);
        Assert.False(viewModel.Disconnect());
    }
}
