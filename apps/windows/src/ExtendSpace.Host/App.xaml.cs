using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace ExtendSpace.Host;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The one session this host can hold. Lives for the whole process.</summary>
    public static SessionStateMachine Session { get; } = new();

    /// <summary>Shared so the selection and its session subscription survive page navigation.</summary>
    public static ConnectViewModel ConnectViewModel { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Created here, on the UI thread, so the view model captures the UI synchronization context.
        ConnectViewModel = new ConnectViewModel(Session, SessionCapabilities.CurrentBuild);

        _window = new MainWindow();
        _window.Activate();
    }
}
