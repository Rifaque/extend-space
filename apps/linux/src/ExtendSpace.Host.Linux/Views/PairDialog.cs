using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ExtendSpace.Host.Linux.Views;

/// <summary>The pairing comparison (D77): the six digits, and Pair only if they match the tablet's.</summary>
public sealed class PairDialog : Window
{
    public PairDialog(string deviceName, string code)
    {
        Title = $"Pair with {deviceName}?";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var pair = new Button { Content = "Pair", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsDefault = true };
        pair.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Check that the tablet shows the same code:", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Name = "PairingCode", Text = code, FontSize = 40, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Choose Pair only if the codes match, then confirm on the tablet too.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, pair } },
            },
        };
    }
}
