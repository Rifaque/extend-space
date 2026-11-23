using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;

namespace ExtendSpace.Core.Tests.Connection;

// Ported from the experiment's tests (Streaming/TimingTests.cs) to the Core copy of the code they test: names
// changed (Protocol is WireFormat, message records end in "Message"), behaviour asserted unchanged.

public class ClockOffsetTests
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
}
