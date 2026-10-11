using Microsoft.UI.Xaml.Controls;

namespace ExtendSpace.Host.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public string VersionText { get; } = $"Version {AppInfo.Version}";
}
