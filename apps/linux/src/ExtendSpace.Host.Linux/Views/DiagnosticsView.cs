using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ExtendSpace.Core.Diagnostics;

namespace ExtendSpace.Host.Linux.Views;

/// <summary>The stream figures each second, from the shared diagnostics (D67), and the host's facts.</summary>
public sealed class DiagnosticsView : UserControl
{
    private readonly StackPanel _stream = new() { Spacing = 4 };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public DiagnosticsView()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        var facts = new StackPanel { Spacing = 4 };
        foreach ((string name, string value) in new[]
        {
            ("Version", App.Version),
            ("Desktop", App.Host.Environment.Describe),
            ("Runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            ("Log", App.LogPath),
        })
        {
            facts.Children.Add(Row(name, value));
        }

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "This computer", Classes = { "title" } },
                    facts,
                    new TextBlock { Text = "Stream", Classes = { "title" } },
                    _stream,
                },
            },
        };
        _refresh.Tick += (_, _) => Refresh();
        AttachedToVisualTree += async (_, _) =>
        {
            Refresh();
            _refresh.Start();
            string adb = await new Core.Transport.AdbDeviceDiscovery(new Core.Transport.AdbProcessRunner()).DescribeAdbAsync(CancellationToken.None);
            facts.Children.Add(Row("adb", adb));
        };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    private void Refresh()
    {
        _stream.Children.Clear();
        foreach (DiagnosticLine line in StreamDiagnosticsText.Lines(App.Host.Diagnostics.Latest))
        {
            _stream.Children.Add(Row(line.Name, line.Value));
        }
    }

    private static Control Row(string name, string value) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("220,*"),
        Children =
        {
            new TextBlock { Text = name, Opacity = 0.75 },
            new SelectableTextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, [Grid.ColumnProperty] = 1 },
        },
    };
}
