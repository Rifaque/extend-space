using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.ViewModels;

namespace ExtendSpace.Host.Linux.Views;

/// <summary>
/// The Connect screen on Linux: the same <see cref="ConnectViewModel"/> as Windows, so the rules
/// (what can be chosen when, what blocks Connect, pairing) are Core's and tested there. This file only
/// moves list selections between the controls and the view model, as the WinUI page does.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private bool _syncing;

    public HomeView()
    {
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            return;
        }

        ViewModel = App.Host.CreateViewModel();
        DataContext = ViewModel;
        Sync();
        ViewModel.PropertyChanged += OnViewModelChanged;
        TransportOptions.SelectionChanged += (_, _) => FromControl(() => ViewModel.SelectedTransport = (TransportKind)TransportOptions.SelectedIndex, TransportOptions.SelectedIndex);
        ModeOptions.SelectionChanged += (_, _) => FromControl(() => ViewModel.SelectedMode = (DisplayMode)ModeOptions.SelectedIndex, ModeOptions.SelectedIndex);
        InputOptions.SelectionChanged += (_, _) => FromControl(() => ViewModel.SelectedInputMode = (Core.Input.InputMode)InputOptions.SelectedIndex, InputOptions.SelectedIndex);
        DeviceList.SelectionChanged += (_, _) => FromControl(() => ViewModel.SelectedDevice = DeviceList.SelectedItem as DeviceOption, 0);
        DisplayList.SelectionChanged += (_, _) => FromControl(() => ViewModel.SelectedDisplay = DisplayList.SelectedItem as DisplayOption, 0);
        AttachedToVisualTree += async (_, _) =>
        {
            // Selecting the initial list items scrolls them into view; the page starts at its top.
            Dispatcher.UIThread.Post(() => Scroller.Offset = default, DispatcherPriority.Loaded);
            ViewModel.RefreshDisplays();
            ViewModel.RefreshWirelessDevices();
            await ViewModel.RefreshDevicesAsync();
        };
    }

    public ConnectViewModel ViewModel { get; } = null!;

    private void FromControl(Action apply, int index)
    {
        if (!_syncing && index >= 0)
        {
            apply();
            Sync();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Sync);

    /// <summary>Puts the view model's choices into the list controls.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            TransportOptions.SelectedIndex = (int)ViewModel.SelectedTransport;
            ModeOptions.SelectedIndex = (int)ViewModel.SelectedMode;
            InputOptions.SelectedIndex = (int)ViewModel.SelectedInputMode;
            if (!ReferenceEquals(DeviceList.SelectedItem, ViewModel.SelectedDevice))
            {
                DeviceList.SelectedItem = ViewModel.SelectedDevice;
            }

            if (!ReferenceEquals(DisplayList.SelectedItem, ViewModel.SelectedDisplay))
            {
                DisplayList.SelectedItem = ViewModel.SelectedDisplay;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e) => ViewModel.Connect();

    private void OnDisconnectClick(object? sender, RoutedEventArgs e) => ViewModel.Disconnect();

    private void OnDismissErrorClick(object? sender, RoutedEventArgs e) => ViewModel.AcknowledgeError();

    private async void OnRefreshDevicesClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.RefreshWirelessDevices();
        await ViewModel.RefreshDevicesAsync();
    }

    private void OnAddAddressClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.AddWirelessAddress(ManualAddress.Text))
        {
            ManualAddress.Text = string.Empty;
        }
    }

    private void OnForgetClick(object? sender, RoutedEventArgs e) => ViewModel.ForgetSelected();

    // D77: the codes are compared here and on the tablet; the dialog is withdrawn if the tablet
    // declines or goes away first.
    private async void OnPairClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.PairSelectedAsync(async (code, deviceName, cancellation) =>
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new PairDialog(deviceName, Core.Wireless.PairingCode.Format(code));
                using CancellationTokenRegistration withdraw = cancellation.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
                bool confirmed = TopLevel.GetTopLevel(this) is Window owner && await dialog.ShowDialog<bool>(owner);
                answer.TrySetResult(confirmed && !cancellation.IsCancellationRequested);
            });
            return await answer.Task.WaitAsync(cancellation);
        });
    }
}
