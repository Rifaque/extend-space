using System.Diagnostics;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;

namespace ExtendSpace.Media.Tests;

/// <summary>
/// The production pipeline with the real backend on this PC: Windows.Graphics.Capture of the
/// primary monitor, the Direct3D 11 video processor and a Media Foundation H.264 encoder. Nothing
/// is sent anywhere and nothing is written to disk; the access units are inspected and dropped.
/// While a test runs, Windows draws its capture border round the monitor.
///
/// Skipped unless <c>EXTENDSPACE_MEDIA_TESTS=1</c>: they need an interactive desktop and a GPU, so
/// they cannot run on a build agent. They prove capture and encoding through the production code
/// on this machine; they prove nothing about a client decoding the result.
/// </summary>
[Trait("Category", "Media")]
public sealed class RealPipelineTests
{
    public RealPipelineTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXTENDSPACE_MEDIA_TESTS") == "1", "real capture and encoding: set EXTENDSPACE_MEDIA_TESTS=1 on a PC with an interactive desktop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_real_pipeline_captures_and_encodes_the_negotiated_stream_starting_with_a_keyframe_and_meets_a_request_on_a_still_screen(bool fallback)
    {
        EncodingRequest request = fallback ? StreamConfigurationPolicy.Fallback : StreamConfigurationPolicy.Preferred;
        var lines = new List<string>();
        var pipeline = new DesktopVideoPipeline(new DesktopVideoPipelineOptions { Log = line => { lock (lines) { lines.Add(line); } } });
        var sink = new RecordingSink();
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            AttemptOutcome<VideoSourceDescription> acquired = await pipeline.AcquireAsync(new VideoSourceRequest(null), CancellationToken.None).Within();
            Assert.Equal(OutcomeKind.Succeeded, acquired.Kind);
            long acquiredMs = watch.ElapsedMilliseconds;

            AttemptOutcome<StreamSettings> prepared = await pipeline.PrepareAsync(request, CancellationToken.None).Within();
            Assert.True(prepared.Kind == OutcomeKind.Succeeded, prepared.Fault?.Message);
            long preparedMs = watch.ElapsedMilliseconds;
            Assert.Equal((request.Width, request.Height, request.FrameRate, request.BitrateBitsPerSecond), (prepared.Value!.Width, prepared.Value.Height, prepared.Value.FrameRate, prepared.Value.BitrateBitsPerSecond));

            long startedAt = watch.ElapsedMilliseconds;
            pipeline.Start(sink.Sink);
            pipeline.SetEncoding(true);
            pipeline.RequestKeyframe();
            await WaitUntil(() => sink.Units.Count > 0, TimeSpan.FromSeconds(5));
            Assert.True(sink.Units[0].Keyframe, "the first access unit must be a keyframe");

            // What the encoder produced, read from its own parameter set, is the stream configured (D57).
            Assert.StartsWith($"{request.Width} x {request.Height}, High profile", pipeline.ProducedStream, StringComparison.Ordinal);

            // Two seconds of whatever the desktop does.
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            VideoPipelineCounters before = pipeline.Counters!;

            // A request with whatever the screen is doing: it must be met, still or not.
            long requestedAt = Stopwatch.GetTimestamp();
            int keyframesBefore = sink.Keyframes;
            pipeline.RequestKeyframe();
            await WaitUntil(() => sink.Keyframes > keyframesBefore, TimeSpan.FromSeconds(2));
            double requestToKeyframeMs = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;

            long stopAt = Stopwatch.GetTimestamp();
            await pipeline.StopAsync(CancellationToken.None).Within();
            double stopMs = Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds;
            VideoPipelineCounters after = pipeline.Counters!;

            string report = string.Join(Environment.NewLine, [
                $"acquire {acquiredMs} ms; prepare {preparedMs - acquiredMs} ms; start to first keyframe {after.FirstKeyframeAfterStart?.TotalMilliseconds:0.0} ms",
                $"source {acquired.Value!.Width} x {acquired.Value.Height} at {acquired.Value.RefreshHz} Hz; stream {prepared.Value}",
                $"produced: {pipeline.ProducedStream}",
                $"after 2 s: {before}",
                $"request to keyframe: {requestToKeyframeMs:0.0} ms; stop and release: {stopMs:0.0} ms",
                $"at the end: {after}",
                .. pipeline.Details,
                .. lines,
            ]);
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            Assert.True(pipeline.IsReleased);
            Assert.True(after.KeyframesDelivered >= 2);
            Assert.False(after.KeyframeOwed);
        }
        finally
        {
            await pipeline.StopAsync(CancellationToken.None).Within();
        }
    }

    [Fact]
    public async Task Acquiring_and_releasing_the_real_pipeline_ten_times_leaves_it_released_each_time()
    {
        var pipeline = new DesktopVideoPipeline();
        for (int round = 0; round < 10; round++)
        {
            var sink = new RecordingSink();
            Assert.Equal(OutcomeKind.Succeeded, (await pipeline.AcquireAsync(new VideoSourceRequest(null), CancellationToken.None).Within()).Kind);
            Assert.Equal(OutcomeKind.Succeeded, (await pipeline.PrepareAsync(StreamConfigurationPolicy.Preferred, CancellationToken.None).Within()).Kind);
            pipeline.Start(sink.Sink);
            pipeline.SetEncoding(true);
            pipeline.RequestKeyframe();
            await WaitUntil(() => sink.Keyframes > 0, TimeSpan.FromSeconds(5));
            await pipeline.StopAsync(CancellationToken.None).Within();
            Assert.True(pipeline.IsReleased, $"round {round}");
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, $"not reached within {within.TotalSeconds:0.#} s");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
