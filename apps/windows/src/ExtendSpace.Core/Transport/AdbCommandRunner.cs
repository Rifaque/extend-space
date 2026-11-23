using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ExtendSpace.Core.Transport;

public enum AdbCommandStatus
{
    /// <summary>adb ran and exited; see the exit code.</summary>
    Exited,

    /// <summary>adb did not exit within the command's time limit and was ended.</summary>
    TimedOut,

    /// <summary>The caller's cancellation was observed; adb was ended.</summary>
    Cancelled,

    /// <summary>No adb executable could be found.</summary>
    NotFound,

    /// <summary>An adb executable was found but could not be started.</summary>
    CouldNotStart,
}

/// <param name="StandardOutput">Cut at a bound, like <paramref name="StandardError"/>. For logs and for parsing <c>adb devices</c>.</param>
public sealed record AdbCommandResult(AdbCommandStatus Status, int ExitCode = -1, string StandardOutput = "", string StandardError = "")
{
    public bool Succeeded => Status == AdbCommandStatus.Exited && ExitCode == 0;
}

/// <summary>Runs one adb command. The seam that lets <see cref="UsbTransport"/> be tested without adb or a device.</summary>
public interface IAdbCommandRunner
{
    /// <summary>
    /// Runs adb with <paramref name="arguments"/> and waits for it, at most <paramref name="timeout"/>.
    /// Never throws for anything adb does; a timeout or a cancellation ends the adb process before
    /// returning.
    /// </summary>
    Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>
/// Runs the real adb executable. Each command is one short-lived process: it is started with no
/// window and no shell, its output and error streams are drained concurrently (so a chatty adb can
/// never block on a full pipe), each is kept only up to a bound, and the process is ended on a
/// timeout or a cancellation.
///
/// Only the command's own process is ended, never its process tree: adb starts its server as a
/// child when none is running, and that server must outlive the command (as in the experiment's
/// <c>AdbForward.Restore</c>).
/// </summary>
public sealed class AdbProcessRunner(string? adbPath = null) : IAdbCommandRunner
{
    /// <summary>adb's output is a few lines; anything beyond this is not kept.</summary>
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>How long an ended process, and its output streams, are waited for.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    /// <summary>The executable that will be run, or null if none can be found now.</summary>
    public string? Executable => adbPath is null ? Locate() : File.Exists(adbPath) ? adbPath : null;

    public async Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return new AdbCommandResult(AdbCommandStatus.Cancelled);
        }

        if (Executable is not { } executable)
        {
            return new AdbCommandResult(AdbCommandStatus.NotFound);
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned no process.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new AdbCommandResult(AdbCommandStatus.CouldNotStart, StandardError: exception.Message);
        }

        using (process)
        {
            // Nothing is ever typed into adb.
            process.StandardInput.Close();
            Task<string> output = ReadBoundedAsync(process.StandardOutput);
            Task<string> error = ReadBoundedAsync(process.StandardError);

            AdbCommandStatus status = AdbCommandStatus.Exited;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                limit.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    status = cancellation.IsCancellationRequested ? AdbCommandStatus.Cancelled : AdbCommandStatus.TimedOut;
                    await EndAsync(process).ConfigureAwait(false);
                }
            }

            // The streams end when every process holding them has exited. A process adb started
            // could in principle hold them open; output is then given up rather than waited for.
            string outputText = await WithinAsync(output).ConfigureAwait(false);
            string errorText = await WithinAsync(error).ConfigureAwait(false);
            int exitCode = status == AdbCommandStatus.Exited ? process.ExitCode : -1;
            return new AdbCommandResult(status, exitCode, outputText, errorText);
        }
    }

    /// <summary>Finds adb.exe in the Android SDK the environment points at, or on PATH.</summary>
    public static string? Locate()
    {
        string executable = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        IEnumerable<string?> sdkRoots =
        [
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
        ];

        IEnumerable<string> candidates = sdkRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root!, "platform-tools", executable))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), executable)));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task EndAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
            // It exited by itself in the meantime.
        }
        catch (Win32Exception)
        {
            // It is exiting already, or cannot be ended; the wait below is bounded either way.
        }

        using var wait = new CancellationTokenSource(ExitWait);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Bounded: a process that does not end when told is left to the operating system.
        }
    }

    private static async Task<string> WithinAsync(Task<string> read)
    {
        Task finished = await Task.WhenAny(read, Task.Delay(ExitWait)).ConfigureAwait(false);
        return finished == read ? await read.ConfigureAwait(false) : string.Empty;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        char[] buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                // Everything is read, so that adb never blocks writing; only the first part is kept.
                int keep = Math.Min(read, MaxOutputChars - kept.Length);
                if (keep > 0)
                {
                    kept.Append(buffer, 0, keep);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe was closed under the reader when the process was disposed: what was read stands.
        }

        return kept.ToString();
    }
}
