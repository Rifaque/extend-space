using ExtendSpace.Core.Connection;

namespace ExtendSpace.Core.Tests.Connection;

public class KeyframeRequestGateTests
{
    private const long Interval = 500_000;

    [Fact]
    public void The_first_request_is_passed_on_and_a_burst_after_it_becomes_one_deferred_request()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));

        Assert.True(gate.Request(1_000_000));
        Assert.Null(gate.DeferredDueMicros);
        Assert.False(gate.Request(1_100_000));
        Assert.False(gate.Request(1_200_000));

        Assert.Equal(1_500_000, gate.DeferredDueMicros);
        Assert.False(gate.TakeDue(1_499_999));
        Assert.True(gate.TakeDue(1_500_000));
        Assert.False(gate.TakeDue(1_600_000));
        Assert.Null(gate.DeferredDueMicros);
    }

    [Fact]
    public void A_reset_for_a_new_stream_drops_the_deferred_request_and_the_interval()
    {
        // D64: a request deferred on an earlier connection is not released on the next one.
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        gate.Request(1_000_000);
        Assert.False(gate.Request(1_100_000));

        gate.Reset();

        Assert.Null(gate.DeferredDueMicros);
        Assert.False(gate.TakeDue(2_000_000));
        Assert.True(gate.Request(1_200_000));
    }

    [Fact]
    public void A_deferred_request_that_has_been_passed_on_starts_a_new_interval()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        gate.Request(0);
        gate.Request(10);
        Assert.True(gate.TakeDue(600_000));

        Assert.False(gate.Request(700_000));
        Assert.Equal(1_100_000, gate.DeferredDueMicros);
    }

    [Fact]
    public void A_request_after_the_interval_is_passed_on_at_once_and_clears_any_deferred_one()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        gate.Request(0);
        gate.Request(100);

        Assert.True(gate.Request(900_000));
        Assert.Null(gate.DeferredDueMicros);
        Assert.False(gate.TakeDue(2_000_000));
    }

    // ---- D62: what each request became, and who it came from ------------------------------

    [Fact]
    public void A_request_passed_at_once_is_told_apart_from_one_deferred_and_from_one_merged_into_a_deferral()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));

        KeyframeGateResult first = gate.Request(1_000_000, KeyframeRequestSource.QueueFull);
        Assert.Equal(new KeyframeGateResult(KeyframeGateDecision.Passed, null, null, null), first);

        KeyframeGateResult deferred = gate.Request(1_080_000, KeyframeRequestSource.Client);
        Assert.Equal(new KeyframeGateResult(KeyframeGateDecision.Deferred, null, 1_500_000, null), deferred);

        KeyframeGateResult merged = gate.Request(1_100_000, KeyframeRequestSource.AwaitingKeyframe);
        Assert.Equal(new KeyframeGateResult(KeyframeGateDecision.Coalesced, null, 1_500_000, null), merged);
        Assert.False(merged.Passed);

        // Exactly one interval after the last pass is no longer too soon.
        Assert.Equal(KeyframeGateDecision.Coalesced, gate.Request(1_499_999, KeyframeRequestSource.Client).Decision);
        Assert.Equal(KeyframeGateDecision.Passed, gate.Request(1_500_000, KeyframeRequestSource.Client).Decision);
    }

    [Fact]
    public void A_released_request_reports_when_it_was_first_deferred_and_every_request_merged_into_it_by_source()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        gate.Request(1_000_000, KeyframeRequestSource.QueueFull);
        gate.Request(1_010_000, KeyframeRequestSource.AwaitingKeyframe);
        gate.Request(1_020_000, KeyframeRequestSource.AwaitingKeyframe);
        gate.Request(1_083_000, KeyframeRequestSource.Client);

        Assert.False(gate.TakeDue(1_499_999, out _));
        Assert.True(gate.TakeDue(1_503_000, out KeyframeGatePending released));
        Assert.Equal(new KeyframeGatePending(1_010_000, 1_500_000, KeyframeRequestSource.AwaitingKeyframe, ClientRequests: 1, HostRequests: 2), released);

        // Released once; the next request is measured from the release.
        Assert.False(gate.TakeDue(2_000_000, out _));
        KeyframeGateResult next = gate.Request(2_100_000, KeyframeRequestSource.Client);
        Assert.Equal(KeyframeGateDecision.Passed, next.Decision);
        Assert.Equal(597_000, next.SinceLastPassMicros);
    }

    [Fact]
    public void A_request_passed_after_the_interval_while_one_waits_absorbs_it_and_nothing_is_released_afterwards()
    {
        var gate = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        gate.Request(0, KeyframeRequestSource.Client);
        gate.Request(100_000, KeyframeRequestSource.Client);

        KeyframeGateResult passed = gate.Request(700_000, KeyframeRequestSource.TooLarge);
        Assert.Equal(KeyframeGateDecision.Passed, passed.Decision);
        Assert.Equal(new KeyframeGatePending(100_000, 500_000, KeyframeRequestSource.Client, 1, 0), passed.Absorbed);
        Assert.False(gate.TakeDue(5_000_000, out _));
    }

    [Fact]
    public void The_gate_decides_exactly_as_the_gate_before_D62_did_over_a_long_mixed_sequence()
    {
        // The 500 ms limit and its deferral are unchanged by D62: the gate, through both its plain
        // and its attributed calls, agrees on every step with a copy of the gate as it was before.
        var reference = new GateBeforeD62(Interval);
        var plain = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        var attributed = new KeyframeRequestGate(TimeSpan.FromMicroseconds(Interval));
        var random = new Random(62);
        long now = 0;
        for (int step = 0; step < 5_000; step++)
        {
            // Steps of 10 ms, so that requests land exactly on the end of the interval often.
            now += random.Next(0, 13) * 10_000;
            if (random.Next(3) == 0)
            {
                bool expected = reference.TakeDue(now);
                Assert.Equal(expected, plain.TakeDue(now));
                Assert.Equal(expected, attributed.TakeDue(now, out _));
            }
            else
            {
                bool expected = reference.Request(now);
                Assert.Equal(expected, plain.Request(now));
                Assert.Equal(expected, attributed.Request(now, (KeyframeRequestSource)random.Next(4)).Passed);
            }

            Assert.Equal(reference.DeferredDueMicros, plain.DeferredDueMicros);
            Assert.Equal(reference.DeferredDueMicros, attributed.DeferredDueMicros);
        }
    }

    /// <summary>The decision logic of <see cref="KeyframeRequestGate"/> before D62, copied unchanged.</summary>
    private sealed class GateBeforeD62(long intervalMicros)
    {
        private long? _lastPassedMicros;
        private bool _deferred;

        public bool Request(long nowMicros)
        {
            if (_lastPassedMicros is { } last && nowMicros - last < intervalMicros)
            {
                _deferred = true;
                return false;
            }

            _lastPassedMicros = nowMicros;
            _deferred = false;
            return true;
        }

        public long? DeferredDueMicros => _deferred && _lastPassedMicros is { } last ? last + intervalMicros : null;

        public bool TakeDue(long nowMicros)
        {
            if (!_deferred || (_lastPassedMicros is { } last && nowMicros - last < intervalMicros))
            {
                return false;
            }

            _deferred = false;
            _lastPassedMicros = nowMicros;
            return true;
        }
    }
}
