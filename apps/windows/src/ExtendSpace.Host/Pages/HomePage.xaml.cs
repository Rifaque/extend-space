using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtendSpace.Host.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();

        // The option lists are ordered like their enums, so index and enum value convert directly.
        TransportOptions.SelectedIndex = (int)ViewModel.SelectedTransport;
        ModeOptions.SelectedIndex = (int)ViewModel.SelectedMode;
    }

    public ConnectViewModel ViewModel { get; } = App.ConnectViewModel;

    public static Visibility VisibleWhenNotEmpty(string text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void OnTransportSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransportOptions.SelectedIndex >= 0)
        {
            ViewModel.SelectedTransport = (TransportKind)TransportOptions.SelectedIndex;
        }
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeOptions.SelectedIndex >= 0)
        {
            ViewModel.SelectedMode = (DisplayMode)ModeOptions.SelectedIndex;
        }
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel.Connect();

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => ViewModel.Disconnect();

    private void OnDismissErrorClick(object sender, RoutedEventArgs e) => ViewModel.AcknowledgeError();
}
