using System.Reflection;
using System.Runtime.InteropServices;
using System.ComponentModel;
using ExtendSpace.Core.Diagnostics;
using ExtendSpace.Core.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtendSpace.Host.Pages;

public sealed record DiagnosticEntry(string Name, string Value);

/// <summary>Real application state, and the stream's figures refreshed once a second while the page is shown (D67).</summary>
public sealed partial class DiagnosticsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public DiagnosticsPage()
    {
        SessionCapabilities capabilities = SessionCapabilities.CurrentBuild;

        ApplicationEntries =
        [
            new("Version", AppInfo.Version),
            new("Session state", App.Session.State.Phase.ToString()),
            new("Windows", Environment.OSVersion.Version.ToString()),
            new("Process architecture", RuntimeInformation.ProcessArchitecture.ToString()),
            new("Runtime", RuntimeInformation.FrameworkDescription),
        ];
        FeatureEntries =
        [
            new("USB transport", Describe(capabilities.Usb)),
            new("Wireless transport", Describe(capabilities.Wireless)),
            new("Mirror", Describe(capabilities.Mirror)),
            new("Extend", Describe(capabilities.Extend)),
        ];

        StreamEntries = Entries();
        InitializeComponent();

        _refresh.Tick += (_, _) =>
        {
            StreamEntries = Entries();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamEntries)));
        };
        Loaded += async (_, _) =>
        {
            _refresh.Start();
            string adb = await new ExtendSpace.Core.Transport.AdbDeviceDiscovery(new ExtendSpace.Core.Transport.AdbProcessRunner()).DescribeAdbAsync(CancellationToken.None);
            ApplicationEntries = [.. ApplicationEntries, new DiagnosticEntry("adb", adb)];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApplicationEntries)));
        };
        Unloaded += (_, _) => _refresh.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DiagnosticEntry> StreamEntries { get; private set; }

    public IReadOnlyList<DiagnosticEntry> ApplicationEntries { get; private set; }

    public IReadOnlyList<DiagnosticEntry> FeatureEntries { get; }

    private static IReadOnlyList<DiagnosticEntry> Entries() =>
        [.. StreamDiagnosticsText.Lines(App.Diagnostics.Latest).Select(line => new DiagnosticEntry(line.Name, line.Value))];

    private static string Describe(Availability availability) =>
        availability.IsAvailable ? "Available" : $"Not available. {availability.Reason}";
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
