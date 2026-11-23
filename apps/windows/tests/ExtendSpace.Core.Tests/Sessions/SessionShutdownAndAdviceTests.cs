using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.ViewModels;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.Sessions;

/// <summary>Closing the app during a session (D70), the advice shown for fixable failures, and adb's version line.</summary>
public sealed class SessionShutdownAndAdviceTests
{
    [Fact]
    public void Closing_needs_a_stop_exactly_while_a_session_is_in_progress_or_ending()
    {
        Assert.False(SessionShutdown.NeedsStop(SessionPhase.Disconnected));
        Assert.False(SessionShutdown.NeedsStop(SessionPhase.Failed));
        foreach (SessionPhase phase in new[] { SessionPhase.Connecting, SessionPhase.Negotiating, SessionPhase.Streaming, SessionPhase.Reconnecting, SessionPhase.Disconnecting })
        {
            Assert.True(SessionShutdown.NeedsStop(phase), phase.ToString());
        }
    }

    [Fact]
    public async Task Closing_stops_the_session_and_returns_when_its_teardown_has_finished()
    {
        var machine = new SessionStateMachine();
        machine.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));
        var control = new MachineSessionControl(machine);

        Task<bool> closing = SessionShutdown.StopAndWaitAsync(control, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(SessionPhase.Disconnecting, machine.State.Phase);
        Assert.False(closing.IsCompleted);

        machine.Dispatch(new SessionEvent.TeardownCompleted());

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_teardown_that_never_finishes_does_not_hold_the_window_beyond_the_bound()
    {
        var machine = new SessionStateMachine();
        machine.Dispatch(new SessionEvent.ConnectRequested(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));

        bool ended = await SessionShutdown.StopAndWaitAsync(new MachineSessionControl(machine), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.False(ended);
        Assert.Equal(SessionPhase.Disconnecting, machine.State.Phase);
    }

    [Fact]
    public async Task With_nothing_in_progress_closing_returns_at_once()
    {
        Assert.True(await SessionShutdown.StopAndWaitAsync(new MachineSessionControl(new SessionStateMachine()), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ConnectionFaultCodes.NoAppListening, "tap Wait for PC")]
    [InlineData(TransportFaultCodes.AdbNotFound, "Platform-Tools")]
    [InlineData(TransportFaultCodes.AdbNotStartable, "Platform-Tools")]
    [InlineData(TransportFaultCodes.DeviceUnauthorized, "allow USB debugging")]
    [InlineData(TransportFaultCodes.DeviceNotAttached, "Check the cable")]
    [InlineData(TransportFaultCodes.SeveralDevices, "Choose the device")]
    [InlineData(ConnectionFaultCodes.KeepaliveTimeout, "stopped answering")]
    [InlineData(ConnectionFaultCodes.SendTimeout, "stopped answering")]
    public void A_failure_the_user_can_fix_says_what_to_do(string code, string words)
    {
        var machine = new SessionStateMachine();
        var viewModel = new ConnectViewModel(new MachineSessionControl(machine), SessionCapabilities.CurrentBuild);
        viewModel.Connect();
        machine.Dispatch(new SessionEvent.Failed(new SessionError(
            SessionErrorCode.TransportUnavailable,
            "the component's own technical sentence",
            new SessionFault(FaultCategory.Transport, code, "the component's own technical sentence", FaultTreatment.Terminal))));

        Assert.Contains(words, viewModel.StatusDetail);
        Assert.DoesNotContain("technical sentence", viewModel.StatusDetail);
    }

    [Fact]
    public void Any_other_failure_keeps_the_components_detail()
    {
        Assert.Null(ConnectViewModel.Advice("encoder_output_mismatch"));
        Assert.Null(ConnectViewModel.Advice(null));
    }

    [Fact]
    public void Adbs_version_output_is_read_into_one_line()
    {
        const string Output = "Android Debug Bridge version 1.0.41\r\nVersion 35.0.2-12147458\r\nInstalled as C:\\sdk\\platform-tools\\adb.exe\r\nRunning on Windows 10.0.26300\r\n";

        Assert.Equal("1.0.41 (Platform-Tools 35.0.2)", AdbDeviceDiscovery.ParseVersion(Output));
        Assert.Equal("1.0.39", AdbDeviceDiscovery.ParseVersion("Android Debug Bridge version 1.0.39\n"));
        Assert.Null(AdbDeviceDiscovery.ParseVersion("something else"));
    }
}
