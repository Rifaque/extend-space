using System.Text;
using ExtendSpace.Experiments.CaptureEncode.Capture;
using ExtendSpace.Experiments.CaptureEncode.Encoding;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Streaming;

namespace ExtendSpace.Experiments.CaptureEncode;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailed = 1;
    private const int ExitUsage = 2;

    // Direct3D, Media Foundation and the capture callbacks are all used from several threads.
    [MTAThread]
    private static int Main(string[] args)
    {
        ParsedCommand command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run with --help for the options.");
            return ExitUsage;
        }

        UseUtf8WhenRedirected();
        DisplayMonitors.EnablePerMonitorDpiAwareness();

        switch (command.Kind)
        {
            case CommandKind.Help:
                Console.WriteLine(CommandLine.HelpText);
                return ExitSuccess;
            case CommandKind.List:
                return List();
            case CommandKind.Stream:
                return Stream(command.Options, command.Stream);
            default:
                return Capture(command.Options);
        }
    }

    /// <summary>
    /// A report saved to a file should keep names such as "Intel®" intact. Redirected output would
    /// otherwise be written in the console's legacy code page. The console itself is left alone.
    /// </summary>
    private static void UseUtf8WhenRedirected()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }

        if (Console.IsErrorRedirected)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
    }

    private static int Capture(CaptureOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C keeps the process alive so the encoder can be drained and the file
            // closed. A second one is left to end the process the usual way.
            if (!cancellation.IsCancellationRequested)
            {
                e.Cancel = true;
                cancellation.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancelKey;
        RunReport report;
        try
        {
            report = new ExperimentRunner(options, Console.Out).Run(cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
        }

        report.Write(Console.Out);
        return report.Succeeded ? ExitSuccess : ExitFailed;
    }

    private static int Stream(CaptureOptions options, StreamOptions stream)
    {
        using var cancellation = new CancellationTokenSource();
        void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            // As for a capture: the first Ctrl+C ends the session in an orderly way.
            if (!cancellation.IsCancellationRequested)
            {
                e.Cancel = true;
                cancellation.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancelKey;
        StreamReport report;
        try
        {
            report = new StreamRunner(options, stream, Console.Out).Run(cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
        }

        report.Write(Console.Out);
        return report.Succeeded ? ExitSuccess : ExitFailed;
    }

    private static int List()
    {
        try
        {
            Console.WriteLine($"Windows.Graphics.Capture supported: {(MonitorCapture.IsSupported ? "yes" : "no")}");

            Console.WriteLine();
            Console.WriteLine("Monitors (--monitor <index>):");
            foreach (DisplayMonitor monitor in DisplayMonitors.Enumerate())
            {
                Console.WriteLine(
                    $"  {monitor.Index}: {monitor.DeviceName}  {monitor.Width} x {monitor.Height} at ({monitor.Left}, {monitor.Top}){(monitor.IsPrimary ? "  [primary]" : string.Empty)}");
            }

            using var mediaFoundation = new MediaFoundationSession();
            using var graphics = GraphicsDevice.Create();

            Console.WriteLine();
            Console.WriteLine($"Graphics adapter used for capture and encoding: {graphics.AdapterName} (vendor 0x{graphics.AdapterVendorId:X4})");

            PrintEncoders("Hardware H.264 encoders on that adapter (tried first, in this order):", EncoderCatalog.FindHardware(graphics.AdapterLuid));
            PrintEncoders("Hardware H.264 encoders on any adapter:", EncoderCatalog.FindHardware(adapter: null));
            PrintEncoders("Software H.264 encoders (the fallback):", EncoderCatalog.FindSoftware());
            return ExitSuccess;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Listing failed. {exception.GetType().Name}: {exception.Message.Trim()} (0x{exception.HResult:X8})");
            return ExitFailed;
        }
    }

    private static void PrintEncoders(string heading, List<EncoderCandidate> candidates)
    {
        Console.WriteLine();
        Console.WriteLine(heading);
        if (candidates.Count == 0)
        {
            Console.WriteLine("  none");
        }

        foreach (EncoderCandidate candidate in candidates)
        {
            Console.WriteLine($"  {candidate.Describe()}");
            candidate.Dispose();
        }
    }
}
