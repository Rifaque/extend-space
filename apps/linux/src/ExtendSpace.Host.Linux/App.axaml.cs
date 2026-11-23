using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ExtendSpace.Linux;

namespace ExtendSpace.Host.Linux;

public sealed class App : Application
{
    public static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The Linux host's composition (D96), created once at start.</summary>
    public static LinuxHost Host { get; private set; } = null!;

    public static string LogPath { get; } = Path.Combine(LinuxHost.DefaultFolder, "host.log");

    private static readonly Lock LogGate = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A process that ends must say why in the log (ES-163): a crash, an unobserved task failure, or a normal exit.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"crash: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"unobserved task failure: {e.Exception.GetBaseException()}");
            e.SetObserved();
        };
        Host = LinuxHost.Create(Version, Log);
        Log($"ExtendSpace {Version} on {Host.Environment.Describe}");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Log("shutdown requested");
            desktop.Exit += (_, e) =>
            {
                Host.Dispose();
                Log($"exited (code {e.ApplicationExitCode})");
            };
        }

        Host.Diagnostics.Start();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Appends to the host log; never throws (a log must not end a session).</summary>
    public static void Log(string line)
    {
        System.Diagnostics.Trace.WriteLine(line);
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
