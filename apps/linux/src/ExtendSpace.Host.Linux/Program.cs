using Avalonia;
using ExtendSpace.Linux;

namespace ExtendSpace.Host.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One ExtendSpace per user (ES-164): a second one would share the tablet's adb forward and, at
        // start, remove the first one's Extend display as a leftover. The lock goes with the process,
        // so a crash never leaves it held.
        using FileStream? instance = InstanceLock.TryAcquire(LinuxHost.DefaultFolder);
        if (instance is null)
        {
            string message = "ExtendSpace is already running for this user. Switch to its window.";
            Console.Error.WriteLine(message);
            App.Log($"second start refused: {message}");
            return 2;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
