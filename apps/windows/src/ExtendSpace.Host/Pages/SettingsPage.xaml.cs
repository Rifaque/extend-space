using Microsoft.UI.Xaml.Controls;

namespace ExtendSpace.Host.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public string VersionText { get; } = $"Version {AppInfo.Version}";

    /// <summary>What a paired tablet lists this PC by; read without creating an identity just to show it.</summary>
    public string WirelessText
    {
        get
        {
            int paired = App.Wireless.Trust.List().Count;
            string tablets = paired == 1 ? "1 paired tablet" : $"{paired} paired tablets";
            return File.Exists(App.Wireless.Identity.Path)
                ? $"This PC's wireless ID: {Core.Wireless.DeviceIds.Short(App.Wireless.Identity.LoadOrCreate().DeviceId)} · {tablets}. Pair and forget tablets on Home, with the Wireless connection chosen."
                : $"No wireless identity yet: it is created the first time this PC pairs with a tablet. {tablets}.";
        }
    }
}
