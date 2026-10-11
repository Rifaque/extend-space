using System.Runtime.InteropServices;
using ExtendSpace.Host.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace ExtendSpace.Host;

public sealed partial class MainWindow : Window
{
    // Sections that exist in the navigation but have no functionality yet.
    private static readonly Dictionary<string, PlaceholderContent> Placeholders = new()
    {
        ["devices"] = new(
            "Devices",
            "",
            "Paired and connected Android devices will be listed here. Pairing is not implemented yet."),
        ["quality"] = new(
            "Quality",
            "",
            "Resolution, frame rate and bitrate controls will live here once video streaming exists."),
        ["display"] = new(
            "Display",
            "",
            "Choosing which display to mirror, and arranging an extended display, will live here."),
    };

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeToDefault(widthDip: 1080, heightDip: 720);

        // Raises SelectionChanged, which performs the first navigation.
        NavView.SelectedItem = NavView.MenuItems[0];
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
                default:
                    ContentFrame.Navigate(typeof(PlaceholderPage), Placeholders[tag], transition);
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
