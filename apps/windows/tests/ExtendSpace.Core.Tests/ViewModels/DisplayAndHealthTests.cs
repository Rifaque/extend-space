using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.ViewModels;

/// <summary>The Connect screen's display choice (D68) and stream-health line (D67).</summary>
public sealed class DisplayAndHealthTests
{
    private static readonly DisplayInfo Main = new(@"\\.\DISPLAY1", true, 2560, 1440, 144);
    private static readonly DisplayInfo Side = new(@"\\.\DISPLAY2", false, 1920, 1080, 60);

    private readonly SessionStateMachine _session = new();
    private readonly ScriptedDisplays _displays = new();

    [Fact]
    public void The_main_display_is_chosen_by_default_and_asked_for_as_the_main_display()
    {
        var control = new RecordingControl(_session);
        var viewModel = new ConnectViewModel(control, SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Now = [Side, Main];

        viewModel.RefreshDisplays();

        Assert.Equal(["Display 1 (main)", "Display 2"], viewModel.Displays.Select(display => display.Title));
        Assert.Equal("2560 × 1440, 144 Hz", viewModel.Displays[0].Detail);
        Assert.True(viewModel.SelectedDisplay!.IsPrimary);
        Assert.True(viewModel.Connect());
        Assert.Null(control.LastRequest!.MonitorDeviceName);
    }

    [Fact]
    public void Another_display_is_asked_for_by_name()
    {
        var control = new RecordingControl(_session);
        var viewModel = new ConnectViewModel(control, SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Now = [Main, Side];
        viewModel.RefreshDisplays();

        viewModel.SelectedDisplay = viewModel.Displays.Single(display => !display.IsPrimary);

        Assert.True(viewModel.Connect());
        Assert.Equal(@"\\.\DISPLAY2", control.LastRequest!.MonitorDeviceName);
    }

    [Fact]
    public void The_choice_is_kept_across_refreshes_and_falls_back_to_the_main_display_when_its_display_goes()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(_session), SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Now = [Main, Side];
        viewModel.RefreshDisplays();
        viewModel.SelectedDisplay = viewModel.Displays[1];

        _displays.Now = [Side with { Width = 1280, Height = 720 }, Main];
        viewModel.RefreshDisplays();
        Assert.Equal(@"\\.\DISPLAY2", viewModel.SelectedDisplay!.DeviceName);
        Assert.Equal("1280 × 720, 60 Hz", viewModel.SelectedDisplay.Detail);

        _displays.Now = [Main];
        viewModel.RefreshDisplays();
        Assert.True(viewModel.SelectedDisplay!.IsPrimary);
    }

    [Fact]
    public void With_no_display_at_all_connect_is_disabled_and_says_why()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(_session), SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Now = [];

        viewModel.RefreshDisplays();

        Assert.Null(viewModel.SelectedDisplay);
        Assert.False(viewModel.CanConnect);
        Assert.Equal($"{HostPlatform.Name} reports no display to mirror.", viewModel.DeviceHint);
    }

    [Fact]
    public void During_a_session_the_display_choice_is_fixed()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(_session), SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Now = [Main, Side];
        viewModel.RefreshDisplays();
        Assert.True(viewModel.Connect());

        viewModel.SelectedDisplay = viewModel.Displays[1];
        _displays.Now = [Side];
        viewModel.RefreshDisplays();

        Assert.True(viewModel.SelectedDisplay!.IsPrimary);
        Assert.Equal(2, viewModel.Displays.Count);
    }

    [Fact]
    public void A_display_catalogue_that_fails_leaves_no_display_rather_than_throwing()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(_session), SessionCapabilities.CurrentBuild, displays: _displays);
        _displays.Fail = true;

        viewModel.RefreshDisplays();

        Assert.Empty(viewModel.Displays);
        Assert.False(viewModel.CanConnect);
    }

    [Theory]
    [InlineData(StreamHealthState.Flowing, false, "Streaming at 60 frames per second.")]
    [InlineData(StreamHealthState.EncoderStalled, true, "encoder has stopped")]
    [InlineData(StreamHealthState.TransportStalled, true, "Check the cable")]
    [InlineData(StreamHealthState.ClientStalled, true, "not showing it")]
    [InlineData(StreamHealthState.NoClientReport, true, "not reported")]
    public void Each_health_state_has_a_sentence_and_only_real_problems_are_flagged(StreamHealthState state, bool problem, string words)
    {
        string? text = StreamDiagnosticsText.Summary(Report(state, null));

        Assert.Contains(words, text);
        Assert.Equal(problem, StreamDiagnosticsText.IsProblem(state));
    }

    [Fact]
    public void A_still_screen_is_described_as_normal_never_as_a_fault()
    {
        Assert.False(StreamDiagnosticsText.IsProblem(StreamHealthState.Quiet));
        Assert.Contains("normal", StreamDiagnosticsText.Summary(Report(StreamHealthState.Quiet, TimeSpan.FromSeconds(75))));
        Assert.Contains("1 min 15 s", StreamDiagnosticsText.Summary(Report(StreamHealthState.Quiet, TimeSpan.FromSeconds(75))));
        Assert.Null(StreamDiagnosticsText.Summary(Report(StreamHealthState.NotStreaming, null)));
    }

    [Fact]
    public void The_diagnostics_table_names_each_figure_where_it_is_measured()
    {
        var snapshot = new StreamDiagnosticsSnapshot(
            SessionPhase.Streaming,
            Report(StreamHealthState.Flowing, TimeSpan.FromMilliseconds(16)),
            new PipelineDiagnosticCounters(600, 600, 0, 600, 1, 1, TimeSpan.FromMilliseconds(16), 60, "1920 x 1080 High 4.2"),
            new HostConnectionCounters { Transmitted = 598, KeyframesTransmitted = 5 },
            new ClientReport(598, 0, 0, 598, 598, 598, 0, 19.5, 27.0, 1),
            new ClockOffset(0, 2_200),
            TimeSpan.FromSeconds(10));

        IReadOnlyList<DiagnosticLine> lines = StreamDiagnosticsText.Lines(snapshot);

        Assert.Equal("flowing", lines.Single(line => line.Name == "Health").Value);
        Assert.Equal("00:00:10", lines.Single(line => line.Name == "Streaming for").Value);
        Assert.Equal("60.0 fps", lines.Single(line => line.Name == "Sent to the device").Value);
        Assert.Equal("mean 19.5 ms, p95 27.0 ms", lines.Single(line => line.Name == "Device: render latency").Value);
        Assert.Equal("2.2 ms", lines.Single(line => line.Name == "Round trip").Value);
        Assert.Equal("Not sampled yet.", Assert.Single(StreamDiagnosticsText.Lines(null)).Value);
    }

    private static StreamHealthReport Report(StreamHealthState state, TimeSpan? sinceLastFrame) =>
        new(state, false, 0, new StreamRates(60, 60, 60, 0, 60, 60), sinceLastFrame, false);

    private sealed class ScriptedDisplays : IDisplayCatalog
    {
        public IReadOnlyList<DisplayInfo> Now { get; set; } = [];

        public bool Fail { get; set; }

        public IReadOnlyList<DisplayInfo> List() => Fail ? throw new InvalidOperationException("EnumDisplayMonitors failed") : Now;
    }

    private sealed class RecordingControl(SessionStateMachine machine) : ISessionControl
    {
        private readonly MachineSessionControl _inner = new(machine);

        public SessionRequest? LastRequest { get; private set; }

        public SessionState State => _inner.State;

        public event EventHandler? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public SessionStartResult Start(SessionRequest request)
        {
            LastRequest = request;
            return _inner.Start(request);
        }

        public bool Stop() => _inner.Stop();

        public bool AcknowledgeError() => _inner.AcknowledgeError();
    }
}
