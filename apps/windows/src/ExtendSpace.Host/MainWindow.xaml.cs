using System.Runtime.InteropServices;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Host.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace ExtendSpace.Host;

public sealed partial class MainWindow : Window
{
    private bool _closeAfterStop;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeToDefault(widthDip: 1080, heightDip: 720);

        // Raises SelectionChanged, which performs the first navigation.
        NavView.SelectedItem = NavView.MenuItems[0];

        AppWindow.Closing += OnClosing;
    }

    // Closing with a session in progress ends the session first (D70): the device is told goodbye and
    // the adb forward is removed, within the teardown's own bound, then the window closes.
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterStop || !SessionShutdown.NeedsStop(App.Session.State.Phase))
        {
            return;
        }

        args.Cancel = true;
        _closeAfterStop = true;
        Title = "ExtendSpace: ending the session";
        await SessionShutdown.StopAndWaitAsync(App.Session, SessionShutdown.DefaultBound);
        App.Diagnostics.Dispose();
        Close();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var transition = new EntranceNavigationTransitionInfo();

        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), null, transition);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            switch (tag)
            {
                case "home":
                    ContentFrame.Navigate(typeof(HomePage), null, transition);
                    break;
                case "diagnostics":
                    ContentFrame.Navigate(typeof(DiagnosticsPage), null, transition);
                    break;
            }
        }
    }

    private void ResizeToDefault(int widthDip, int heightDip)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
