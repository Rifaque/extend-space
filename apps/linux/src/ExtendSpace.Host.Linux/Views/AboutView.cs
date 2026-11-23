using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ExtendSpace.Host.Linux.Views;

/// <summary>What this Linux build is and what it can do here (D98, D100), stated plainly.</summary>
public sealed class AboutView : UserControl
{
    public AboutView()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        ExtendSpace.Linux.LinuxEnvironment environment = App.Host.Environment;
        string support = environment.Server switch
        {
            ExtendSpace.Linux.DisplayServer.Wayland when environment.IsWlroots =>
                "Mirror through the desktop's sharing dialog. Extend and input are not available on wlroots desktops.",
            ExtendSpace.Linux.DisplayServer.Wayland =>
                "Mirror and Extend through the desktop's sharing dialog (you allow it once per connection). Touch and Mouse through the desktop's remote-control permission: touch is real touchscreen input, confined to the shared display.",
            ExtendSpace.Linux.DisplayServer.X11 =>
                "Mirror any monitor. Extend adds a virtual area right of your screens while connected (experimental). Input: Mouse only (X11 has no touch injection without root).",
            _ => "No graphical session was found.",
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"ExtendSpace {App.Version} for Linux (beta)", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Running on {environment.Describe}.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = support, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "USB needs adb (android-tools). Video needs GStreamer with an H.264 encoder (x264 from plugins-ugly, or VA-API/NVENC).", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                new TextBlock { Text = $"Data: {ExtendSpace.Linux.LinuxHost.DefaultFolder}", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            },
        };
    }
}
