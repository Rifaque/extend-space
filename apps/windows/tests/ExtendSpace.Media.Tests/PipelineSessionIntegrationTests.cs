using System.Diagnostics;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;

namespace ExtendSpace.Media.Tests;

/// <summary>
/// The production session as the host composes it, minus the hardware: the real
/// <see cref="SessionCoordinator"/>, the real <see cref="HostConnectionController"/> over a loopback
/// socket to a stand-in client, and the real <see cref="DesktopVideoPipeline"/> with its own pump
/// thread over a scripted capture source and encoder. What these show is that video produced by the
/// pipeline reaches a client through the connection in the right order: a usable keyframe first on
/// every connection, a keyframe on request and after a reconnection even when the screen has not
/// changed, a bounded queue that drops to the next keyframe, and teardown that stops the pipeline
/// before the connection. They show nothing about real capture, a real encoder or a device.
/// </summary>
public sealed class PipelineSessionIntegrationTests : IAsyncLifetime
{
    private readonly LoopbackTransport _transport = new();
    private readonly ScriptedBackend _backend = new(new ManualTimestamp());
    private readonly List<string> _connectionLog = [];
    private DesktopVideoPipeline _pipeline = null!;
    private HostConnectionController _connection = null!;
    private SessionCoordinator _coordinator = null!;

    public ValueTask InitializeAsync()
    {
        _pipeline = new DesktopVideoPipeline(
            _backend,
            new DesktopVideoPipelineOptions { RaiseTimerResolution = false, PollInterval = TimeSpan.FromMilliseconds(5) },
            ownPumpThread: true,
            timestamp: null);
        _connection = new HostConnectionController(_transport, new HostConnectionOptions
        {
            DeviceName = "TEST-HOST",
            AppVersion = "1.2.3",
            Log = line =>
            {
                lock (_connectionLog)
                {
                    _connectionLog.Add(line);
                }
            },
        });
        _transport.Release = _ =>
        {
            _backend.Record("transport release");
            return Task.CompletedTask;
        };
        _coordinator = new SessionCoordinator(new SessionStateMachine(), _connection, _pipeline, new SystemSessionClock(), new SessionCoordinatorOptions { RetryBudget = TimeSpan.FromSeconds(8) });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator.Stop())
        {
            await WaitForAsync(state => state.Phase is SessionPhase.Disconnected);
        }

        _transport.Dispose();
        Assert.Equal(0, _coordinator.UnhandledInputFailures);
    }

    [Fact]
    public async Task The_first_video_on_a_connection_is_a_self_contained_keyframe_numbered_zero_then_ordinary_frames_follow()
    {
        using TestClient client = await StreamingAsync();
        _backend.Source.Arrive();
        var first = await client.NextVideoAsync();
        Assert.Equal(((ushort)(VideoFlags.Keyframe | VideoFlags.Config), 0u), (first.Header.Flags, first.Sequence));
        Assert.True(H264Units.IsKeyframe(first.AccessUnit));

        _backend.Source.Arrive();
        var second = await client.NextVideoAsync();
        Assert.Equal(((ushort)0, 1u), (second.Header.Flags, second.Sequence));

        // The encoder was opened for exactly what was negotiated: the client's decoder takes 1080p60.
        Assert.Equal(StreamConfigurationPolicy.Preferred, _backend.Source.Encoder.Request);
        Assert.Contains("open source primary", _backend.Log);
    }

    [Fact]
    public async Task After_a_reconnection_on_a_still_screen_the_new_connection_starts_with_a_keyframe_made_from_the_last_picture()
    {
        TestClient client = await StreamingAsync();
        _backend.Source.Arrive();
        await client.NextVideoAsync();
        _backend.Source.Arrive();
        await client.NextVideoAsync();
        int framesBefore = _backend.Source.Frames.Count;

        client.CloseAbruptly();
        client.Dispose();
        await WaitForAsync(state => state.Phase == SessionPhase.Reconnecting);

        using TestClient returning = await _transport.AcceptAsync();
        await returning.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming);

        // Nothing on the screen changed, yet the returning client gets a keyframe, numbered from zero.
        var first = await returning.NextVideoAsync();
        Assert.Equal(((ushort)(VideoFlags.Keyframe | VideoFlags.Config), 0u), (first.Header.Flags, first.Sequence));
        Assert.Equal(framesBefore, _backend.Source.Frames.Count);
        Assert.True(_pipeline.Counters!.FramesRepeated >= 1);

        // The source and the encoder were kept across the reconnection, not reopened.
        Assert.Single(_backend.Sources);
        Assert.Single(_backend.Source.Encoders);
    }

    [Fact]
    public async Task A_keyframe_the_client_asks_for_on_a_still_screen_arrives_without_the_screen_changing()
    {
        using TestClient client = await StreamingAsync();
        _backend.Source.Arrive();
        await client.NextVideoAsync();
        _backend.Source.Arrive();
        Assert.Equal(0, (await client.NextVideoAsync()).Header.Flags);

        await client.SendAsync(new KeyframeRequestMessage("surface_recreated"));
        var answer = await client.NextVideoAsync();
        Assert.Equal((ushort)(VideoFlags.Keyframe | VideoFlags.Config), answer.Header.Flags);
        Assert.Equal(2u, answer.Sequence);
        Assert.Equal(2, _backend.Source.Frames.Count);
    }

    [Fact]
    public async Task Stopping_stops_and_releases_the_pipeline_before_the_connection_says_goodbye_and_releases_the_transport()
    {
        using TestClient client = await StreamingAsync();
        _backend.Source.Arrive();
        await client.NextVideoAsync();

        Assert.True(_coordinator.Stop());
        await client.ExpectAsync<ByeMessage>();
        client.CloseAfterSending();
        await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);

        IReadOnlyList<string> log = _backend.Log;
        int sourceReleased = log.ToList().IndexOf("source dispose");
        Assert.True(log.ToList().IndexOf("encoder dispose") < sourceReleased);
        Assert.True(sourceReleased < log.ToList().IndexOf("transport release"), string.Join(", ", log));
        Assert.True(_pipeline.IsReleased);
        Assert.True(_coordinator.IsReleased);
        Assert.Empty(_coordinator.LastTeardownFaults);
    }

    [Fact]
    public async Task A_capture_source_that_fails_while_streaming_ends_the_session_failed_and_the_client_is_told()
    {
        using TestClient client = await StreamingAsync();
        _backend.Source.Arrive();
        await client.NextVideoAsync();

        _backend.Source.Failure = "The captured monitor was closed by Windows (disconnected or no longer available).";
        _backend.Source.Arrive();
        ErrorMessage error = await client.ExpectAsync<ErrorMessage>();
        Assert.True(error.Fatal);

        SessionState failed = await WaitForAsync(state => state.Phase == SessionPhase.Failed);
        Assert.Equal(SessionErrorCode.CaptureUnavailable, failed.Error!.Code);
        Assert.Equal(MediaFaultCodes.CaptureFailed, failed.Error.Cause!.Code);
        Assert.True(_pipeline.IsReleased);
        _coordinator.AcknowledgeError();
    }

    [Fact]
    public async Task A_client_that_stops_reading_makes_the_bounded_queue_drop_to_the_next_keyframe_and_stop_is_still_prompt()
    {
        using TestClient client = await StreamingAsync();
        _backend.Source.Encoder.Padding = 256 * 1024;

        // The client reads nothing while the host produces 15 MB.
        for (int i = 0; i < 60; i++)
        {
            _backend.Source.Arrive();
            await Task.Delay(8, TestContext.Current.CancellationToken);
        }

        HostConnectionCounters counters = _connection.Counters;
        Assert.True(counters.DroppedQueueFull + counters.DroppedAwaitingKeyframe > 0, $"nothing was dropped: {counters}");

        // D64: the overflow's keyframe is asked for once the writer can send again, not while it is
        // blocked, so here it may or may not have been asked yet; the startup request always was.
        Assert.True(_pipeline.Counters!.KeyframeRequests >= 1, "the stream's start must ask the pipeline for a keyframe");

        // Whatever the client then reads, a gap in the numbering is always followed by a keyframe.
        var received = new List<(uint Sequence, ushort Flags)>();
        _backend.Source.Encoder.Padding = 0;
        _backend.Source.Arrive();
        Stopwatch reading = Stopwatch.StartNew();
        while (reading.Elapsed < TimeSpan.FromMilliseconds(1500))
        {
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            Task<(FrameHeader Header, uint Sequence, long Timestamp, byte[] AccessUnit)> next = client.NextVideoAsync();
            if (await Task.WhenAny(next, Task.Delay(300, slice.Token)) != next)
            {
                break;
            }

            var frame = await next;
            received.Add((frame.Sequence, frame.Header.Flags));
            _backend.Source.Arrive();
        }

        Assert.NotEmpty(received);
        Assert.Equal((ushort)(VideoFlags.Keyframe | VideoFlags.Config), received[0].Flags);
        for (int i = 1; i < received.Count; i++)
        {
            if (received[i].Sequence != received[i - 1].Sequence + 1)
            {
                Assert.Equal((ushort)(VideoFlags.Keyframe | VideoFlags.Config), received[i].Flags);
            }
        }

        Assert.Contains(received.Skip(1), frame => frame.Flags != 0);
        Assert.True(_pipeline.Counters!.KeyframeRequests >= 2, "dropping frames must ask the pipeline for a keyframe once the writer is free");

        long stopAt = Stopwatch.GetTimestamp();
        Assert.True(_coordinator.Stop());
        await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
        Assert.True(Stopwatch.GetElapsedTime(stopAt) < TimeSpan.FromSeconds(3), $"stop took {Stopwatch.GetElapsedTime(stopAt).TotalMilliseconds:0} ms");
        Assert.True(_pipeline.IsReleased);
    }

    [Fact]
    public async Task Ten_sessions_in_a_row_each_get_a_first_keyframe_and_each_release_their_source_and_encoder()
    {
        for (int round = 0; round < 10; round++)
        {
            using (TestClient client = await StreamingAsync())
            {
                _backend.Source.Arrive();
                var first = await client.NextVideoAsync();
                Assert.Equal(((ushort)(VideoFlags.Keyframe | VideoFlags.Config), 0u), (first.Header.Flags, first.Sequence));
                Assert.True(_coordinator.Stop());
                await client.ExpectAsync<ByeMessage>();
                client.CloseAfterSending();
            }

            await WaitForAsync(state => state.Phase == SessionPhase.Disconnected);
            Assert.True(_pipeline.IsReleased, $"round {round}");
        }

        Assert.Equal(10, _backend.Sources.Count);
        Assert.All(_backend.Sources, source => Assert.Equal((1, 1), (source.Disposals, source.Encoder.Disposals)));
    }

    private async Task<TestClient> StreamingAsync()
    {
        Assert.Equal(SessionStartResult.Started, _coordinator.Start(new SessionRequest(TransportKind.Usb, DisplayMode.Mirror)));
        TestClient client = await _transport.AcceptAsync();
        await client.HandshakeAsync();
        await WaitForAsync(state => state.Phase == SessionPhase.Streaming);
        return client;
    }

    private async Task<SessionState> WaitForAsync(Func<SessionState, bool> condition)
    {
        var reached = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            SessionState state = _coordinator.State;
            if (condition(state))
            {
                reached.TrySetResult(state);
            }
        }

        _coordinator.StateChanged += Check;
        try
        {
            Check(null, EventArgs.Empty);
            return await reached.Task.Within();
        }
        finally
        {
            _coordinator.StateChanged -= Check;
        }
    }
}
