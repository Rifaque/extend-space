using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Transport;

/// <summary>
/// The process side of the adb runner, with stand-in executables that ship with Windows: that a
/// command's exit code and output are read, that a chatty process cannot block on a full pipe, and
/// that a timeout or a cancellation ends the process promptly. They need Windows and no adb. The
/// one test that runs the real adb is skipped unless EXTENDSPACE_ADB_TESTS=1.
/// </summary>
[Trait("Category", "WindowsProcess")]
public sealed class AdbProcessRunnerTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_command_is_run_without_a_shell_and_its_exit_code_and_output_are_returned()
    {
        string script = Script("@echo out-line\r\n@echo err-line 1>&2\r\n@exit /b 3\r\n");

        AdbCommandResult result = await Cmd().RunAsync(["/c", script], TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal((AdbCommandStatus.Exited, 3), (result.Status, result.ExitCode));
        Assert.Contains("out-line", result.StandardOutput);
        Assert.Contains("err-line", result.StandardError);
    }

    [Fact]
    public async Task A_process_that_writes_a_great_deal_to_both_streams_cannot_block_on_a_full_pipe()
    {
        // About 400 KB on each stream, far beyond a pipe's buffer. Read one after the other, or
        // not at all, this would hang; drained together it exits.
        string script = Script("@for /L %%i in (1,1,8000) do @(echo stdout line %%i of some length to fill the pipe quickly & echo stderr line %%i of some length to fill the pipe quickly 1>&2)\r\n");

        AdbCommandResult result = await Cmd().RunAsync(["/c", script], TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal((AdbCommandStatus.Exited, 0), (result.Status, result.ExitCode));
        Assert.Equal(64 * 1024, result.StandardOutput.Length);
        Assert.Equal(64 * 1024, result.StandardError.Length);
    }

    [Fact]
    public async Task A_command_that_runs_past_its_time_limit_is_ended_and_reported_as_timed_out()
    {
        long took = await Loopback.TimeAsync(async () =>
        {
            AdbCommandResult result = await Ping().RunAsync(["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Assert.Equal(AdbCommandStatus.TimedOut, result.Status);
        });

        // Ending the process is waited for, up to 2 s: returning well inside that means it ended.
        Assert.True(took < 1_500, $"the timed-out command took {took} ms to return");
    }

    [Fact]
    public async Task A_cancelled_command_is_ended_promptly_and_reported_as_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        Task<AdbCommandResult> running = Ping().RunAsync(["-n", "30", "127.0.0.1"], TimeSpan.FromSeconds(60), cancel.Token);
        await Task.WhenAny(running, Task.Delay(200, TestContext.Current.CancellationToken));
        Assert.False(running.IsCompleted);

        long took = await Loopback.TimeAsync(async () =>
        {
            cancel.Cancel();
            Assert.Equal(AdbCommandStatus.Cancelled, (await running.Within()).Status);
        });

        Assert.True(took < 1_500, $"the cancelled command took {took} ms to return");
    }

    [Fact]
    public async Task A_missing_executable_is_reported_as_not_found_and_nothing_is_started()
    {
        var runner = new AdbProcessRunner(Path.Combine(Path.GetTempPath(), $"no-such-adb-{Guid.NewGuid():N}.exe"));

        AdbCommandResult result = await runner.RunAsync(["devices"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AdbCommandStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task The_real_adb_reports_its_version()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_ADB_TESTS") == "1", "set EXTENDSPACE_ADB_TESTS=1 to run against the adb on this PC (needs no device)");
        var runner = new AdbProcessRunner();
        Assert.SkipWhen(runner.Executable is null, "adb was not found through ANDROID_HOME, ANDROID_SDK_ROOT or PATH");

        // 'adb version' neither starts the adb server nor touches a device.
        AdbCommandResult result = await runner.RunAsync(["version"], TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.Succeeded, $"{result.Status} {result.ExitCode}: {result.StandardError}");
        Assert.Contains("Android Debug Bridge", result.StandardOutput);
    }

    private static AdbProcessRunner Cmd()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses cmd.exe");
        return new AdbProcessRunner(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
    }

    private static AdbProcessRunner Ping()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses ping.exe");
        return new AdbProcessRunner(Path.Combine(Environment.SystemDirectory, "PING.EXE"));
    }

    private string Script(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"extendspace-test-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }
}
