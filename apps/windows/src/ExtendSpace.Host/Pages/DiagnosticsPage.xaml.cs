using System.Reflection;
using System.Runtime.InteropServices;
using ExtendSpace.Core.Sessions;
using Microsoft.UI.Xaml.Controls;

namespace ExtendSpace.Host.Pages;

public sealed record DiagnosticEntry(string Name, string Value);

/// <summary>A snapshot of real application state, taken when the page is opened.</summary>
public sealed partial class DiagnosticsPage : Page
{
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

        InitializeComponent();
    }

    public IReadOnlyList<DiagnosticEntry> ApplicationEntries { get; }

    public IReadOnlyList<DiagnosticEntry> FeatureEntries { get; }

    private static string Describe(Availability availability) =>
        availability.IsAvailable ? "Available" : $"Not available. {availability.Reason}";
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
