using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Input;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Core.Tests.ViewModels;

/// <summary>D106: a host can say an input mode cannot work there; the screen shows why and never selects it.</summary>
public sealed class InputAvailabilityTests : IDisposable
{
    private readonly HostInput _input = new(new FixedMonitor(new ScreenRect(0, 0, 1920, 1080)), () => new RecordingTouch(), new RecordingMouse(), () => null, autoTick: false);

    private sealed class NoTouch : IInputAvailability
    {
        public string? Unavailable(InputMode mode) => mode == InputMode.Touch ? "No touch here." : null;

        public string? Note => "Mouse goes through the compositor.";
    }

    public void Dispose() => _input.Dispose();

    [Fact]
    public void An_unavailable_mode_says_why_on_its_option_and_is_not_selected()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(new SessionStateMachine()), SessionCapabilities.CurrentBuild, input: _input, inputAvailability: new NoTouch());

        Assert.Equal("No touch here.", viewModel.InputOptions[(int)InputMode.Touch].StatusNote);
        Assert.Equal(string.Empty, viewModel.InputOptions[(int)InputMode.Mouse].StatusNote);

        viewModel.SelectedInputMode = InputMode.Touch;
        Assert.Equal(InputMode.Off, viewModel.SelectedInputMode);
        Assert.Equal(InputMode.Off, _input.Mode);
        Assert.Equal("No touch here.", viewModel.InputNote);

        viewModel.SelectedInputMode = InputMode.Mouse;
        Assert.Equal(InputMode.Mouse, _input.Mode);
        Assert.Equal("Mouse goes through the compositor.", viewModel.InputNote);
    }

    [Fact]
    public void Without_an_availability_every_mode_is_offered_as_before()
    {
        var viewModel = new ConnectViewModel(new MachineSessionControl(new SessionStateMachine()), SessionCapabilities.CurrentBuild, input: _input);

        Assert.All(viewModel.InputOptions, option => Assert.Equal(string.Empty, option.StatusNote));
        viewModel.SelectedInputMode = InputMode.Touch;
        Assert.Equal(InputMode.Touch, _input.Mode);
    }
}
