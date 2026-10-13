using ExtendSpace.Experiments.CaptureEncode.Streaming;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Streaming;

public class TimingTests
{
    [Fact]
    public void The_offset_assumes_the_pong_was_stamped_halfway_through_the_round_trip()
    {
        var estimator = new ClockOffsetEstimator();

        // The ping leaves at 1,000,000 and the pong is back at 1,002,000: the client stamped it at
        // about host time 1,001,000. Its own clock read 51,001,000 then, so it is 50 s ahead.
        Assert.True(estimator.Add(hostSendMicros: 1_000_000, hostReceiveMicros: 1_002_000, clientMicros: 51_001_000));

        Assert.Equal(new ClockOffset(50_000_000, 2_000), estimator.Best);
    }

    [Fact]
    public void The_true_offset_always_lies_within_half_the_round_trip_of_the_estimate()
    {
        const long TrueOffset = -7_654_321;
        var random = new Random(99);

        for (int i = 0; i < 200; i++)
        {
            long send = 10_000_000 + (i * 1_000_000L);
            long outbound = random.Next(50, 5_000);
            long inbound = random.Next(50, 5_000);
            var estimator = new ClockOffsetEstimator();

            estimator.Add(send, send + outbound + inbound, send + outbound + TrueOffset);

            ClockOffset best = Assert.NotNull(estimator.Best);
            Assert.True(Math.Abs(best.OffsetMicros - TrueOffset) <= (best.RoundTripMicros / 2) + 1);
        }
    }

    [Fact]
    public void The_sample_with_the_shortest_round_trip_is_the_one_trusted()
    {
        var estimator = new ClockOffsetEstimator();
        estimator.Add(0, 9_000, 1_004_500);
        estimator.Add(100_000, 100_400, 1_100_200);
        estimator.Add(200_000, 205_000, 1_202_500);

        Assert.Equal(new ClockOffset(1_000_000, 400), estimator.Best);
        Assert.Equal(3, estimator.Samples);
    }

    [Fact]
    public void Old_samples_fall_out_of_the_window_so_that_clock_drift_is_followed()
    {
        var estimator = new ClockOffsetEstimator(window: 3);
        estimator.Add(0, 100, 1_000_050);
        estimator.Add(1_000, 2_000, 2_001_500);
        estimator.Add(2_000, 3_000, 2_002_500);
        estimator.Add(3_000, 4_000, 2_003_500);

        Assert.Equal(new ClockOffset(2_000_000, 1_000), estimator.Best);
    }

    [Fact]
    public void A_pong_that_claims_to_precede_its_ping_is_ignored()
    {
        var estimator = new ClockOffsetEstimator();

        Assert.False(estimator.Add(hostSendMicros: 5_000, hostReceiveMicros: 4_000, clientMicros: 1));

        Assert.Null(estimator.Best);
        Assert.Equal(0, estimator.Samples);
    }

    [Fact]
    public void Reconnection_waits_double_from_a_quarter_second_to_four_seconds()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(30));
        var delays = new List<double>();
        TimeSpan elapsed = TimeSpan.Zero;

        while (backoff.NextDelay(elapsed) is { } delay)
        {
            delays.Add(delay.TotalSeconds);
            elapsed += delay;
        }

        Assert.Equal([0.25, 0.5, 1, 2, 4, 4, 4, 4, 4, 4, 2.25], delays);
        Assert.Equal(30, delays.Sum());
    }

    [Fact]
    public void Reconnection_is_abandoned_once_the_budget_is_spent()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(30));

        Assert.NotNull(backoff.NextDelay(TimeSpan.FromSeconds(29.9)));
        Assert.Null(backoff.NextDelay(TimeSpan.FromSeconds(30)));
        Assert.Null(backoff.NextDelay(TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void A_successful_connection_starts_the_waits_from_the_beginning_again()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(30));
        backoff.NextDelay(TimeSpan.Zero);
        backoff.NextDelay(TimeSpan.Zero);
        backoff.NextDelay(TimeSpan.Zero);
        Assert.Equal(3, backoff.Attempts);

        backoff.Reset();

        Assert.Equal(ReconnectBackoff.InitialDelay, backoff.NextDelay(TimeSpan.Zero));
        Assert.Equal(1, backoff.Attempts);
    }

    [Fact]
    public void A_burst_of_keyframe_requests_costs_one_keyframe()
    {
        var limiter = new KeyframeRequestLimiter(TimeSpan.FromMilliseconds(500));

        Assert.True(limiter.TryAcquire(1_000_000));
        Assert.False(limiter.TryAcquire(1_000_001));
        Assert.False(limiter.TryAcquire(1_499_999));
        Assert.True(limiter.TryAcquire(1_500_000));
        Assert.False(limiter.TryAcquire(1_700_000));
    }

    [Fact]
    public void Capture_timestamps_and_the_host_clock_are_both_in_microseconds()
    {
        Assert.Equal(1_500_000, HostClock.ToMicros(TimeSpan.FromSeconds(1.5)));
        Assert.Equal(1, HostClock.ToMicros(TimeSpan.FromTicks(10)));

        long before = HostClock.NowMicros;
        long after = HostClock.NowMicros;
        Assert.True(after >= before);
    }

    [Fact]
    public void Latency_reports_combine_into_an_exact_mean_and_the_extremes()
    {
        var aggregate = new LatencyAggregate();
        aggregate.Add(new LatencySummary(Count: 60, MinMs: 10, MeanMs: 20, P95Ms: 30, MaxMs: 40));
        aggregate.Add(new LatencySummary(Count: 30, MinMs: 8, MeanMs: 26, P95Ms: 50, MaxMs: 90));
        aggregate.Add(new LatencySummary(Count: 10, MinMs: 12, MeanMs: 40, P95Ms: 35, MaxMs: 45));

        Assert.Equal(100, aggregate.Frames);
        Assert.Equal(3, aggregate.Reports);
        Assert.Equal(((60 * 20) + (30 * 26) + (10 * 40)) / 100.0, aggregate.MeanMs);
        Assert.Equal(8, aggregate.MinMs);
        Assert.Equal(90, aggregate.MaxMs);
        Assert.Equal(35, aggregate.MedianP95Ms);
        Assert.Equal(50, aggregate.WorstP95Ms);
    }

    [Fact]
    public void Latency_that_was_never_reported_stays_unavailable()
    {
        var aggregate = new LatencyAggregate();
        aggregate.Add(null);
        aggregate.Add(new LatencySummary(0, 0, 0, 0, 0));

        Assert.Equal(0, aggregate.Frames);
        Assert.Null(aggregate.MeanMs);
        Assert.Null(aggregate.MinMs);
        Assert.Null(aggregate.MaxMs);
        Assert.Null(aggregate.MedianP95Ms);
        Assert.Null(aggregate.WorstP95Ms);
    }

    [Fact]
    public void A_copy_of_the_latency_figures_does_not_follow_later_reports()
    {
        var aggregate = new LatencyAggregate();
        aggregate.Add(new LatencySummary(10, 1, 2, 3, 4));
        LatencyAggregate copy = aggregate.Copy();

        aggregate.Add(new LatencySummary(10, 100, 200, 300, 400));

        Assert.Equal(10, copy.Frames);
        Assert.Equal(2, copy.MeanMs);
        Assert.Equal(3, copy.WorstP95Ms);
    }

    [Fact]
    public void Host_counters_account_for_every_encoded_frame()
    {
        var counters = new HostStreamCounters
        {
            Encoded = 1000,
            NotSentNoClient = 40,
            DroppedAwaitingKeyframe = 12,
            DroppedQueueFull = 5,
            DiscardedAtDisconnect = 2,
            DroppedTooLarge = 1,
            Transmitted = 940,
        };

        Assert.Equal(0, counters.Unaccounted);
        Assert.Equal(3, (counters with { Transmitted = 937 }).Unaccounted);
    }

    [Fact]
    public void Client_counters_from_separate_connections_add_up()
    {
        var first = new ClientStats { FramesReceived = 100, BytesReceived = 5_000, FramesDecoded = 99, FramesRendered = 99, FramesMissing = 1, QueueDepth = 3 };
        var second = new ClientStats { FramesReceived = 50, BytesReceived = 2_500, FramesDecoded = 50, FramesRendered = 49, DecoderErrors = 1 };

        ClientStats total = first.Plus(second);

        Assert.Equal((150, 7_500, 149, 148, 1, 1), (total.FramesReceived, total.BytesReceived, total.FramesDecoded, total.FramesRendered, total.FramesMissing, total.DecoderErrors));
        Assert.Equal(0, total.QueueDepth);
        Assert.Null(total.RenderLatency);
    }
}
