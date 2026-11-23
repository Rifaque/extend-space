using ExtendSpace.Core.Connection;

namespace ExtendSpace.Core.Tests.Connection;

/// <summary>
/// The host's keyframe-request log lines (D62), from the real gate with time given by hand: each
/// gate outcome has its own event, host and client requests are told apart, merged host requests
/// are counted rather than logged, and nothing claims that the encoder answered a request.
/// </summary>
public class KeyframeRequestTraceTests
{
    private const long Interval = 500_000;

    private readonly KeyframeRequestGate _gate = new(TimeSpan.FromMicroseconds(Interval));
    private readonly KeyframeRequestTrace _trace = new();
    private readonly List<string> _lines = [];

    [Fact]
    public void A_request_passed_at_once_and_one_deferred_are_logged_as_different_events_with_their_source()
    {
        Request(1_000_000, KeyframeRequestSource.Client);
        Request(1_083_000, KeyframeRequestSource.Client);

        Assert.Equal(
            [
                "keyframe gate_passed t=1000000 source=client since_last_pass_ms=none",
                "keyframe gate_deferred t=1083000 source=client due=1500000 wait_ms=417.0",
            ],
            _lines);
    }

    [Fact]
    public void A_client_request_deferred_behind_a_host_request_shows_both_sources_and_the_wait()
    {
        // The shape suspected in D61's 417 ms round: the host's own drop-triggered request passed
        // first, and the client's came 83 ms later.
        Request(1_000_000, KeyframeRequestSource.QueueFull);
        Request(1_083_000, KeyframeRequestSource.Client);

        Assert.StartsWith("keyframe gate_passed t=1000000 source=host_queue_full ", _lines[0]);
        Assert.Equal("keyframe gate_deferred t=1083000 source=client due=1500000 wait_ms=417.0", _lines[1]);
    }

    [Fact]
    public void Host_requests_merged_into_a_waiting_one_are_not_logged_one_by_one_but_client_ones_are()
    {
        Request(0, KeyframeRequestSource.QueueFull);
        Request(10_000, KeyframeRequestSource.AwaitingKeyframe);
        for (int i = 1; i <= 20; i++)
        {
            Request(10_000 + (i * 16_000), KeyframeRequestSource.AwaitingKeyframe);
        }

        Request(400_000, KeyframeRequestSource.Client);

        Assert.Equal(
            [
                "keyframe gate_passed t=0 source=host_queue_full since_last_pass_ms=none",
                "keyframe gate_deferred t=10000 source=host_awaiting_keyframe due=500000 wait_ms=490.0",
                "keyframe gate_coalesced t=400000 source=client due=500000 wait_ms=100.0",
            ],
            _lines);
    }

    [Fact]
    public void A_deferred_request_is_logged_as_released_only_when_the_writer_takes_it_with_every_request_merged_into_it()
    {
        Request(0, KeyframeRequestSource.QueueFull);
        Request(10_000, KeyframeRequestSource.AwaitingKeyframe);
        Request(20_000, KeyframeRequestSource.AwaitingKeyframe);
        Request(83_000, KeyframeRequestSource.Client);
        Assert.DoesNotContain(_lines, line => line.Contains("gate_released", StringComparison.Ordinal));

        // Not yet due: nothing released, nothing logged.
        Assert.False(TakeDue(499_000));
        Assert.DoesNotContain(_lines, line => line.Contains("gate_released", StringComparison.Ordinal));

        Assert.True(TakeDue(502_500));
        Assert.Equal("keyframe gate_released t=502500 due=500000 late_ms=2.5 waited_ms=492.5 first_source=host_awaiting_keyframe client=1 host=2", _lines[^1]);
        Assert.False(TakeDue(600_000));
        Assert.Single(_lines, line => line.Contains("gate_released", StringComparison.Ordinal));
    }

    [Fact]
    public void A_waiting_request_answered_by_a_later_pass_is_reported_as_absorbed_and_never_as_released()
    {
        Request(0, KeyframeRequestSource.Client);
        Request(100_000, KeyframeRequestSource.AwaitingKeyframe);
        Request(650_000, KeyframeRequestSource.Client);

        Assert.Equal("keyframe gate_passed t=650000 source=client since_last_pass_ms=650.0 absorbed_client=0 absorbed_host=1 absorbed_waited_ms=550.0", _lines[^1]);
        Assert.False(TakeDue(2_000_000));
        Assert.DoesNotContain(_lines, line => line.Contains("gate_released", StringComparison.Ordinal));
    }

    [Fact]
    public void Passing_a_request_on_logs_no_encoder_keyframe_until_the_encoder_hands_one_over()
    {
        Request(1_000_000, KeyframeRequestSource.Client);
        Assert.DoesNotContain(_lines, line => line.Contains("encoder_keyframe", StringComparison.Ordinal) || line.Contains("keyframe sent", StringComparison.Ordinal));

        // A keyframe written with none logged as offered is not reported as an answer.
        Assert.Null(_trace.KeyframeSent(1_001_000));

        Assert.Equal(
            "keyframe encoder_keyframe t=1020000 capture=1012000 captured_after_last_raise=yes since_last_raise_ms=20.0 since_first_raise_ms=20.0 raises=1 send=queued",
            _trace.KeyframeOffered(1_020_000, 1_012_000, "queued"));
        Assert.Equal("keyframe sent t=1023000 since_offered_ms=3.0 since_last_raise_ms=23.0", _trace.KeyframeSent(1_023_000));
        Assert.Null(_trace.KeyframeSent(1_040_000));
    }

    [Fact]
    public void Only_the_first_keyframe_after_requests_is_logged_and_it_counts_every_request_passed_on_since_the_last()
    {
        Request(1_000_000, KeyframeRequestSource.QueueFull);
        Request(1_100_000, KeyframeRequestSource.Client);
        Assert.True(TakeDue(1_500_000));

        string? line = _trace.KeyframeOffered(1_520_000, 990_000, "queued");
        Assert.Equal("keyframe encoder_keyframe t=1520000 capture=990000 captured_after_last_raise=no since_last_raise_ms=20.0 since_first_raise_ms=520.0 raises=2 send=queued", line);

        // A periodic keyframe with no request outstanding is not logged.
        Assert.Null(_trace.KeyframeOffered(3_500_000, 3_490_000, "queued"));
    }

    [Fact]
    public void A_keyframe_the_send_queue_dropped_is_logged_as_offered_with_why_and_never_as_sent()
    {
        Request(0, KeyframeRequestSource.Client);
        Assert.EndsWith("send=dropped_too_large", _trace.KeyframeOffered(30_000, 20_000, "dropped_too_large"));
        Assert.Null(_trace.KeyframeSent(31_000));
    }

    [Fact]
    public void A_new_stream_forgets_requests_passed_on_to_the_last_one()
    {
        Request(0, KeyframeRequestSource.Client);
        _trace.Reset();
        Assert.Null(_trace.KeyframeOffered(900_000, 880_000, "queued"));
    }

    [Fact]
    public void The_client_request_line_carries_its_reason_and_receive_time()
    {
        Assert.Equal("keyframe client_request t=42 reason=sequence_gap", KeyframeRequestTrace.ClientRequest(42, "sequence_gap"));
    }

    // As the controller does: ask the gate, log its decision, and record the raise if it passed.
    private void Request(long now, KeyframeRequestSource source)
    {
        KeyframeGateResult result = _gate.Request(now, source);
        if (KeyframeRequestTrace.GateDecision(now, source, result) is { } line)
        {
            _lines.Add(line);
        }

        if (result.Passed)
        {
            _trace.Raised(now);
        }
    }

    private bool TakeDue(long now)
    {
        if (!_gate.TakeDue(now, out KeyframeGatePending released))
        {
            return false;
        }

        _lines.Add(KeyframeRequestTrace.Released(now, released));
        _trace.Raised(now);
        return true;
    }
}
