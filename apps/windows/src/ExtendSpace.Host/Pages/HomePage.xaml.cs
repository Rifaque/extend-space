using System.ComponentModel;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExtendSpace.Host.Pages;

public sealed partial class HomePage : Page
{
    /// <summary>How often the device list is read while a device can be chosen (D65).</summary>
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _deviceRefresh = new() { Interval = DeviceRefreshInterval };
    private bool _syncingSelection;

    public HomePage()
    {
        InitializeComponent();

        // The option lists are ordered like their enums, so index and enum value convert directly.
        TransportOptions.SelectedIndex = (int)ViewModel.SelectedTransport;
        ModeOptions.SelectedIndex = (int)ViewModel.SelectedMode;
        InputOptions.SelectedIndex = (int)ViewModel.SelectedInputMode;

        _deviceRefresh.Tick += OnDeviceRefreshTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ConnectViewModel ViewModel { get; } = App.ConnectViewModel;

    public static Visibility VisibleWhenNotEmpty(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush HealthBrush(bool problem) =>
        (Brush)Application.Current.Resources[problem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RefreshDisplays();
        SyncDeviceSelection();
        SyncDisplaySelection();
        if (ViewModel.ShowsDevices)
        {
            _deviceRefresh.Start();
            await ViewModel.RefreshDevicesAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _deviceRefresh.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async void OnDeviceRefreshTick(object? sender, object e)
    {
        // Only while a device can be chosen: during a session the choice is fixed.
        if (ViewModel.CanEditSelection)
        {
            ViewModel.RefreshDisplays();
            await ViewModel.RefreshDevicesAsync();
        }
    }

    private async void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDisplays();
        await ViewModel.RefreshDevicesAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(ConnectViewModel.SelectedDevice) or nameof(ConnectViewModel.Devices))
        {
            SyncDeviceSelection();
        }

        if (e.PropertyName is null or "" or nameof(ConnectViewModel.SelectedDisplay) or nameof(ConnectViewModel.Displays))
        {
            SyncDisplaySelection();
        }
    }

    private void SyncDisplaySelection()
    {
        if (!ReferenceEquals(DisplayList.SelectedItem, ViewModel.SelectedDisplay))
        {
            _syncingSelection = true;
            try
            {
                DisplayList.SelectedItem = ViewModel.SelectedDisplay;
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    private void OnDisplaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && DisplayList.SelectedItem is DisplayOption chosen)
        {
            ViewModel.SelectedDisplay = chosen;
        }
    }

    // The list's selection follows the view model's: the list is rebuilt on every refresh, and the
    // view model may choose the only device by itself.
    private void SyncDeviceSelection()
    {
        if (!ReferenceEquals(DeviceList.SelectedItem, ViewModel.SelectedDevice))
        {
            _syncingSelection = true;
            try
            {
                DeviceList.SelectedItem = ViewModel.SelectedDevice;
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A refresh replaces the items, which clears the list's selection for a moment; only a
        // device actually chosen is passed on, and the view model's choice is put back afterwards.
        if (!_syncingSelection && DeviceList.SelectedItem is DeviceOption chosen)
        {
            ViewModel.SelectedDevice = chosen;
        }
    }

    private void OnTransportSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransportOptions.SelectedIndex >= 0)
        {
            ViewModel.SelectedTransport = (TransportKind)TransportOptions.SelectedIndex;
        }
    }

    private void OnInputSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InputOptions.SelectedIndex >= 0)
        {
            ViewModel.SelectedInputMode = (Core.Input.InputMode)InputOptions.SelectedIndex;
        }
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeOptions.SelectedIndex >= 0)
        {
            ViewModel.SelectedMode = (DisplayMode)ModeOptions.SelectedIndex;
        }
    }

    // D77: the user compares the six digits with the tablet's and confirms here; the tablet's user
    // confirms there. The dialog is withdrawn if the tablet declines or goes away first.
    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.PairSelectedAsync(async (code, deviceName, cancellation) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Pair with {deviceName}?",
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "Check that the tablet shows the same code:", TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = Core.Wireless.PairingCode.Format(code), FontSize = 40, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock { Text = "Choose Pair only if the codes match, then confirm on the tablet too.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                        },
                    },
                    PrimaryButtonText = "Pair",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                using CancellationTokenRegistration withdraw = cancellation.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                ContentDialogResult answer = await dialog.ShowAsync();
                tcs.TrySetResult(answer == ContentDialogResult.Primary && !cancellation.IsCancellationRequested);
            });
            return await tcs.Task.WaitAsync(cancellation);
        });
    }

    private void OnForgetClick(object sender, RoutedEventArgs e) => ViewModel.ForgetSelected();

    private void OnAddAddressClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AddWirelessAddress(ManualAddress.Text))
        {
            ManualAddress.Text = string.Empty;
        }
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel.Connect();

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => ViewModel.Disconnect();

    private void OnDismissErrorClick(object sender, RoutedEventArgs e) => ViewModel.AcknowledgeError();

    private void OnApplyModeClick(object sender, RoutedEventArgs e) => ViewModel.ApplyRecommendedMode();

    private void OnBringWindowsBackClick(object sender, RoutedEventArgs e) => ViewModel.BringWindowsBack();
}
