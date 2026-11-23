using System.Collections.Concurrent;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Linux.Gstreamer;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux.Tests;

/// <summary>
/// Lab tests: real GStreamer, X server, XTest and xrandr on Linux. They are run on an Xvfb server
/// (WSL in this project's lab) with <c>EXTENDSPACE_LINUX_LAB=1</c>; they are not desktop acceptance
/// (GNOME, KDE and real X11 desktops are the owner's checklist).
/// </summary>
public sealed class LabTests
{
    private static string Display()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("EXTENDSPACE_LINUX_LAB") == "1", "Linux lab: set EXTENDSPACE_LINUX_LAB=1 with DISPLAY on an X server and GStreamer installed");
        return Environment.GetEnvironmentVariable("DISPLAY") ?? throw new InvalidOperationException("DISPLAY is not set");
    }

    private sealed class FixedSource(CaptureSource source, VideoSourceDescription description) : ICaptureSourceProvider
    {
        public int Releases { get; private set; }

        public Task<(CaptureSource Source, VideoSourceDescription Description)> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation) => Task.FromResult((source, description));

        public Task ReleaseAsync()
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_pipeline_streams_H264_access_units_from_an_X_region_starting_with_a_keyframe_and_honours_keyframe_requests()
    {
        string display = Display();
        var source = new FixedSource(new CaptureSource.X11Region(display, new ScreenRect(0, 0, 1280, 720)), new VideoSourceDescription(1280, 720, 60));
        var pipeline = new GstVideoPipeline(source, log: line => TestContext.Current.SendDiagnosticMessage(line));
        var units = new ConcurrentQueue<(bool Key, int Length, long Micros)>();

        Assert.Equal(OutcomeKind.Succeeded, (await pipeline.AcquireAsync(new VideoSourceRequest(null, "screen"), TestContext.Current.CancellationToken)).Kind);
        AttemptOutcome<StreamSettings> prepared = await pipeline.PrepareAsync(new EncodingRequest(1280, 720, 30, 4_000_000), TestContext.Current.CancellationToken);
        Assert.Equal(OutcomeKind.Succeeded, prepared.Kind);
        pipeline.Start((au, micros) => units.Enqueue((AnnexB.IsKeyframe(au), au.Length, micros)));
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        int before = units.Count;
        int keysBefore = units.Count(u => u.Key);
        pipeline.RequestKeyframe();
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        var after = units.Skip(before).ToList();
        await pipeline.StopAsync(TestContext.Current.CancellationToken);

        TestContext.Current.SendDiagnosticMessage($"LAB pipeline: {prepared.Value!.EncoderName}; {before} units in 2 s ({keysBefore} keyframes), {after.Count} in the next 1 s with {after.Count(u => u.Key)} keyframes; counters {pipeline.DiagnosticCounters}");
        Assert.True(units.First().Key, "the first access unit is a keyframe");
        Assert.InRange(before, 40, 70);
        Assert.Contains(after, u => u.Key);
        Assert.Equal(1, source.Releases);
        Assert.True(units.Select(u => u.Micros).Zip(units.Select(u => u.Micros).Skip(1)).All(p => p.Second >= p.First), "timestamps never go back");
    }

    [Fact]
    public async Task Encoding_off_delivers_nothing_and_on_resumes()
    {
        string display = Display();
        var pipeline = new GstVideoPipeline(new FixedSource(new CaptureSource.X11Region(display, new ScreenRect(0, 0, 640, 360)), new VideoSourceDescription(640, 360, 60)));
        int delivered = 0;
        await pipeline.AcquireAsync(new VideoSourceRequest(null), TestContext.Current.CancellationToken);
        await pipeline.PrepareAsync(new EncodingRequest(640, 360, 30, 2_000_000), TestContext.Current.CancellationToken);
        pipeline.SetEncoding(false);
        pipeline.Start((_, _) => Interlocked.Increment(ref delivered));
        await Task.Delay(800, TestContext.Current.CancellationToken);
        int whileOff = delivered;
        pipeline.SetEncoding(true);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        await pipeline.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, whileOff);
        Assert.True(delivered > 10);
    }

    [Fact]
    public void RandR_monitors_are_listed_and_XTest_moves_the_pointer()
    {
        string display = Display();
        IReadOnlyList<X11Monitor> monitors = new XrandrMonitorSource(display).Monitors()!;
        TestContext.Current.SendDiagnosticMessage($"LAB monitors: {string.Join("; ", monitors)}");
        Assert.NotEmpty(monitors);

        using var mouse = new XTestMouseInjector(display);
        Assert.True(mouse.IsAvailable);
        mouse.Move(new ScreenPoint(321, 123));
        (int code, string output) = new ProcessCommandRunner().Run("xdotool", "getmouselocation");
        TestContext.Current.SendDiagnosticMessage($"LAB pointer: {output.Trim()}");
        Assert.Equal(0, code);
        Assert.StartsWith("x:321 y:123", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_RandR_virtual_display_grows_the_framebuffer_and_restores_it()
    {
        string display = Display();
        var runner = new ProcessCommandRunner();
        X11Screen before = X11VirtualDisplay.ParseScreen(runner.Run("xrandr", "--display", display).Output)!;
        var virtualDisplay = new X11VirtualDisplay(runner, display);

        ScreenRect region;
        try
        {
            region = virtualDisplay.Create(640, 480);
        }
        catch (InvalidOperationException e)
        {
            // Xvfb's screen cannot grow beyond its start size: record and stop here.
            TestContext.Current.SendDiagnosticMessage($"LAB randr: {e.Message}");
            Assert.Equal(before, X11VirtualDisplay.ParseScreen(runner.Run("xrandr", "--display", display).Output));
            return;
        }

        X11Screen during = X11VirtualDisplay.ParseScreen(runner.Run("xrandr", "--display", display).Output)!;
        string monitors = runner.Run("xrandr", "--display", display, "--listmonitors").Output;
        virtualDisplay.Remove();
        X11Screen afterwards = X11VirtualDisplay.ParseScreen(runner.Run("xrandr", "--display", display).Output)!;
        TestContext.Current.SendDiagnosticMessage($"LAB randr: {before} -> {during} region {region}; monitors: {monitors.Replace('\n', ' ')}; restored {afterwards}");

        Assert.Equal(before.Width + 640, during.Width);
        Assert.Equal(before, afterwards);
    }
}
