using System.Globalization;

namespace ExtendSpace.Core.Connection;

/// <summary>
/// The host's keyframe-request log lines (decision D62): what each request was, what the gate did
/// with it, when a deferred one was released, and the first keyframe the encoder handed over and
/// the first written to the socket after a request was passed on. Diagnostics only: nothing here
/// changes what is requested, passed on or sent.
///
/// <para><b>Lines.</b> Each is <c>keyframe &lt;event&gt; t=&lt;µs&gt; key=value…</c>, with <c>t</c>
/// on the host's monotonic clock (<see cref="MonotonicClock"/>, the performance counter that capture
/// timestamps and pings use) and durations in milliseconds. Steady streaming logs nothing; a
/// recovery logs a handful of lines. Requests the host makes for itself and that the gate merges
/// into one already waiting are not logged one by one (one is made for every frame refused while the
/// queue waits for a keyframe); they are counted and reported when the waiting request is released
/// or answered.</para>
///
/// <para><b>Attribution.</b> The encoder's keyframe is not tied to a request: the wire protocol has
/// no request ids, the pipeline merges every request made while one is owed, and the encoder also
/// makes a keyframe every 2 s by itself. <c>encoder_keyframe</c> only says it is the first one after
/// the requests counted in <c>raises</c>.</para>
/// </summary>
internal sealed class KeyframeRequestTrace
{
    private readonly Lock _gate = new();

    // Under _gate: requests passed on (KeyframeWanted raised) since the encoder last handed over a keyframe.
    private int _raises;
    private long _firstRaiseMicros;
    private long _lastRaiseMicros;

    // Under _gate: a keyframe that was offered after a raise and queued, not yet written.
    private long? _queuedKeyframeMicros;
    private long _queuedKeyframeRaiseMicros;

    public static string ClientRequest(long nowMicros, string reason) =>
        $"keyframe client_request t={nowMicros} reason={reason}";

    /// <summary>The gate's decision, or null for a host request merged into one already waiting.</summary>
    public static string? GateDecision(long nowMicros, KeyframeRequestSource source, KeyframeGateResult result)
    {
        switch (result.Decision)
        {
            case KeyframeGateDecision.Passed:
                string since = result.SinceLastPassMicros is { } s ? Ms(s) : "none";
                string absorbed = result.Absorbed is { } a
                    ? $" absorbed_client={a.ClientRequests} absorbed_host={a.HostRequests} absorbed_waited_ms={Ms(nowMicros - a.SinceMicros)}"
                    : string.Empty;
                return $"keyframe gate_passed t={nowMicros} source={Name(source)} since_last_pass_ms={since}{absorbed}";
            case KeyframeGateDecision.Deferred:
                return $"keyframe gate_deferred t={nowMicros} source={Name(source)} due={result.DueMicros} wait_ms={Ms(result.DueMicros!.Value - nowMicros)}";
            case KeyframeGateDecision.Coalesced when source == KeyframeRequestSource.Client:
                return $"keyframe gate_coalesced t={nowMicros} source={Name(source)} due={result.DueMicros} wait_ms={Ms(result.DueMicros!.Value - nowMicros)}";
            default:
                return null;
        }
    }

    /// <summary>A deferred request released by the writer and passed on now.</summary>
    public static string Released(long nowMicros, KeyframeGatePending released) =>
        $"keyframe gate_released t={nowMicros} due={released.DueMicros} late_ms={Ms(nowMicros - released.DueMicros)} waited_ms={Ms(nowMicros - released.SinceMicros)} first_source={Name(released.FirstSource)} client={released.ClientRequests} host={released.HostRequests}";

    /// <summary>
    /// D64: the send queue overflowed and a keyframe is owed, to be asked for when the writer is
    /// free. Once per overflow recovery; later overflows before the writer is free add nothing.
    /// </summary>
    /// <param name="dropped">Frames dropped by this overflow, including the one offered.</param>
    /// <param name="keptKeyframe">The queue kept a keyframe and the frames after it.</param>
    public static string RecoveryOwed(long nowMicros, int dropped, bool keptKeyframe) =>
        $"keyframe recovery_owed t={nowMicros} dropped={dropped} kept_keyframe={(keptKeyframe ? "yes" : "no")}";

    /// <summary>
    /// D64: the writer was free again. <paramref name="stillAwaiting"/>: the queue still waits for a
    /// keyframe, so one is asked for now (the gate's line follows); otherwise a keyframe was queued
    /// meanwhile and nothing is asked.
    /// </summary>
    public static string RecoveryWriterFree(long nowMicros, long owedSinceMicros, bool stillAwaiting) =>
        $"keyframe recovery_writer_free t={nowMicros} owed_ms={Ms(nowMicros - owedSinceMicros)} action={(stillAwaiting ? "request" : "none_keyframe_queued")}";

    /// <summary>A new stream: requests passed on to an earlier one are not answered by this one's keyframes.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _raises = 0;
            _queuedKeyframeMicros = null;
        }
    }

    /// <summary>A request was passed on: <see cref="Sessions.ISessionConnection.KeyframeWanted"/> is about to be raised.</summary>
    public void Raised(long nowMicros)
    {
        lock (_gate)
        {
            if (_raises++ == 0)
            {
                _firstRaiseMicros = nowMicros;
            }

            _lastRaiseMicros = nowMicros;
        }
    }

    /// <summary>
    /// The encoder handed over a keyframe. A line only for the first one after a request was passed
    /// on; periodic keyframes with no request outstanding are not logged.
    /// </summary>
    /// <param name="sendOutcome">What the send queue did with it: queued, or why it was dropped.</param>
    public string? KeyframeOffered(long nowMicros, long captureMicros, string sendOutcome)
    {
        lock (_gate)
        {
            if (_raises == 0)
            {
                return null;
            }

            string line = $"keyframe encoder_keyframe t={nowMicros} capture={captureMicros} captured_after_last_raise={(captureMicros >= _lastRaiseMicros ? "yes" : "no")} since_last_raise_ms={Ms(nowMicros - _lastRaiseMicros)} since_first_raise_ms={Ms(nowMicros - _firstRaiseMicros)} raises={_raises} send={sendOutcome}";
            _queuedKeyframeMicros = sendOutcome == "queued" ? nowMicros : null;
            _queuedKeyframeRaiseMicros = _lastRaiseMicros;
            _raises = 0;
            return line;
        }
    }

    /// <summary>A keyframe was written to the socket. A line only for the one <see cref="KeyframeOffered"/> logged.</summary>
    public string? KeyframeSent(long nowMicros)
    {
        lock (_gate)
        {
            if (_queuedKeyframeMicros is not { } offered)
            {
                return null;
            }

            _queuedKeyframeMicros = null;
            return $"keyframe sent t={nowMicros} since_offered_ms={Ms(nowMicros - offered)} since_last_raise_ms={Ms(nowMicros - _queuedKeyframeRaiseMicros)}";
        }
    }

    public static string Name(KeyframeRequestSource source) => source switch
    {
        KeyframeRequestSource.Client => "client",
        KeyframeRequestSource.QueueFull => "host_queue_full",
        KeyframeRequestSource.AwaitingKeyframe => "host_awaiting_keyframe",
        KeyframeRequestSource.TooLarge => "host_too_large",
        _ => source.ToString(),
    };

    private static string Ms(long micros) => (micros / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}
