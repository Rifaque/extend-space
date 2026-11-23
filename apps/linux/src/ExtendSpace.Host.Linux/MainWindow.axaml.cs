using Avalonia.Controls;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Host.Linux;

public sealed partial class MainWindow : Window
{
    private bool _closingAfterStop;

    public MainWindow()
    {
        InitializeComponent();

        // D70: a session is stopped (bounded) before the window closes, so the tablet is told and the
        // adb forward and a virtual display are removed.
        Closing += async (_, e) =>
        {
            if (_closingAfterStop || !SessionShutdown.NeedsStop(App.Host.Session.State.Phase))
            {
                return;
            }

            e.Cancel = true;
            _closingAfterStop = true;
            await SessionShutdown.StopAndWaitAsync(App.Host.Session, SessionShutdown.DefaultBound);
            Close();
        };
    }
}
