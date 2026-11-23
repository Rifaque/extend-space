using System.Collections.Concurrent;
using System.Net.Sockets;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Connection;

public sealed record HostConnectionOptions
{
    /// <summary>This PC's name, as the client is told it in <c>hello</c>.</summary>
    public string DeviceName { get; init; } = Environment.MachineName;

    public string AppVersion { get; init; } = "0.1.0";

    /// <summary>The connect step: the experiment's 2 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Each handshake step (protocol section 7). Shortened only by tests.</summary>
    public TimeSpan HandshakeStepTimeout { get; init; } = WireFormat.HandshakeStepTimeout;

    /// <summary>How long nothing may arrive from the client, or a write may stay blocked, before the connection counts as lost.</summary>
    public TimeSpan KeepaliveTimeout { get; init; } = WireFormat.KeepaliveTimeout;

    public TimeSpan KeepaliveInterval { get; init; } = WireFormat.KeepaliveInterval;

    /// <summary>Encoded frames waiting for the socket (protocol section 9: the experiment's 4).</summary>
    public int SendQueueCapacity { get; init; } = 4;

    /// <summary>How long the writer is given to let go of the socket when the connection ends; beyond it the writer is taken to be blocked.</summary>
    public TimeSpan WriterExitWait { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The bound on a last <c>bye</c> or <c>error</c>, and on waiting for the client to close after it.</summary>
    public TimeSpan FinalMessageTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The bound on the reader and writer finishing once the socket is closed.</summary>
    public TimeSpan LoopJoinTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The H.264 profile <c>stream_config</c> names. <see cref="StreamSettings"/> does not carry one
    /// yet; this is the experiment's default, which its encoder was run with. Stage 4 is to take it
    /// from the encoder that was actually opened.
    /// </summary>
    public string StreamProfile { get; init; } = "high";

    /// <summary>Lines for a diagnostic log. Never video, and never more than a short excerpt of what the peer sent.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Touch from the client (D81–D84). When set and not off, each stream offers input, and the
    /// client's touch frames on that stream are handed to it; null keeps the input channel closed.
    /// </summary>
    public Input.HostInput? Input { get; init; }

    /// <summary>True when the session streams a virtual monitor (Extend, D90): <c>stream_config.mode</c> says so.</summary>
    public Func<bool>? Extending { get; init; }
}

/// <summary>Where each encoded frame went; every offered frame is counted once in exactly one outcome.</summary>
public sealed record HostConnectionCounters
{
    public long Offered { get; init; }

    /// <summary>Offered while no stream was configured; never queued.</summary>
    public long NotSentNoStream { get; init; }

    public long DroppedAwaitingKeyframe { get; init; }

    public long DroppedQueueFull { get; init; }

    public long DroppedTooLarge { get; init; }

    /// <summary>Queued or being written when the connection ended.</summary>
    public long DiscardedAtDisconnect { get; init; }

    public long Transmitted { get; init; }

    public long KeyframesTransmitted { get; init; }

    /// <summary>Times <see cref="ISessionConnection.KeyframeWanted"/> was raised.</summary>
    public long KeyframesWanted { get; init; }

    public long StreamsConfigured { get; init; }

    public long Unaccounted => Offered - NotSentNoStream - DroppedAwaitingKeyframe - DroppedQueueFull - DroppedTooLarge - DiscardedAtDisconnect - Transmitted;
}

/// <summary>
/// The production host's connection to the client (architecture section 3.1): the real
/// <see cref="ISessionConnection"/>. It is the experiment's <c>HostSession</c> without its retry
/// loop, its phase or its statistics: one call is one attempt, and the coordinator decides
/// everything else.
///
/// <para><b>What it owns.</b> The transport (through <see cref="IHostTransport"/>), from the first
/// <see cref="OpenAsync"/> of a session to <see cref="CloseAsync"/>; and at most one connection at a
/// time: its socket, its send queue, and while a stream is configured a reader loop and a writer
/// loop. Each failed or cancelled step closes the connection it was using; <see cref="OpenAsync"/>
/// closes any left over; <see cref="CloseAsync"/> closes it and releases the transport.</para>
///
/// <para><b>Cancellation.</b> Every wait is on the socket, and every cancellation closes the
/// socket: a registration on the caller's token disposes it, which ends any receive, send or connect
/// in progress at once (decision D33). A cancelled step reports <see cref="OutcomeKind.Cancelled"/>,
/// never a fault. A step that completes just as it is cancelled reports success only if its
/// connection survived; otherwise it reports the cancellation.</para>
///
/// <para><b>Threads.</b> None of its own. Socket I/O is asynchronous; the reader and writer loops are
/// tasks on the thread pool. Each public call runs off the caller's thread, so that neither adb nor
/// a socket is touched on the UI thread, and its task completes asynchronously.</para>
/// </summary>
public sealed class HostConnectionController : ISessionConnection
{
    private const int FastPings = 5;
    private static readonly TimeSpan FastPingInterval = TimeSpan.FromMilliseconds(100);

    private readonly IHostTransport _transport;
    private readonly HostConnectionOptions _options;
    private readonly Lock _gate = new();
    private readonly ParameterSetTracker _parameterSets = new();
    private readonly KeyframeRequestGate _keyframes = new(KeyframeRequestGate.DefaultInterval);
    private readonly KeyframeRequestTrace _keyframeTrace = new();

    // Under _gate. The generation changes at every CloseAsync, so that a call that outlives the
    // session's close knows that what it made belongs to nobody.
    private int _generation;
    private Link? _current;

    // Set while a stream is configured; read by OfferVideo on the pipeline's thread.
    private volatile Link? _streaming;

    private long _offered;
    private long _notSentNoStream;
    private long _droppedAwaitingKeyframe;
    private long _droppedQueueFull;
    private long _droppedTooLarge;
    private long _discardedAtDisconnect;
    private long _transmitted;
    private long _keyframesTransmitted;
    private long _keyframesWanted;
    private long _streamsConfigured;
    private ClientReport? _latestClientReport;

    public HostConnectionController(IHostTransport transport, HostConnectionOptions? options = null)
    {
        _transport = transport;
        _options = options ?? new HostConnectionOptions();
    }

    public event Action? KeyframeWanted;

    public HostConnectionCounters Counters => new()
    {
        Offered = Interlocked.Read(ref _offered),
        NotSentNoStream = Interlocked.Read(ref _notSentNoStream),
        DroppedAwaitingKeyframe = Interlocked.Read(ref _droppedAwaitingKeyframe),
        DroppedQueueFull = Interlocked.Read(ref _droppedQueueFull),
        DroppedTooLarge = Interlocked.Read(ref _droppedTooLarge),
        DiscardedAtDisconnect = Interlocked.Read(ref _discardedAtDisconnect),
        Transmitted = Interlocked.Read(ref _transmitted),
        KeyframesTransmitted = Interlocked.Read(ref _keyframesTransmitted),
        KeyframesWanted = Interlocked.Read(ref _keyframesWanted),
        StreamsConfigured = Interlocked.Read(ref _streamsConfigured),
    };

    /// <summary>
    /// The client's latest <c>stats</c> on the current stream, as it reported them: what it received,
    /// decoded and rendered. Null until the first arrives after a stream is configured. The host only
    /// records them; diagnostics that act on them are stage 6.
    /// </summary>
    public ClientReport? LatestClientReport => Volatile.Read(ref _latestClientReport);

    /// <summary>The clock relation of the current connection, once a pong has come back.</summary>
    public ClockOffset? Clock
    {
        get
        {
            lock (_gate)
            {
                return _current?.Clock.Best;
            }
        }
    }

    /// <summary>Whether a connection is held: opened and not yet discarded or closed. For tests and diagnostics.</summary>
    public bool HasConnection
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    // ---- The contract --------------------------------------------------------------------------

    public Task<AttemptOutcome> OpenAsync(CancellationToken cancellation) => OpenAsyncCore(cancellation);

    public Task<AttemptOutcome<ClientCapabilities>> ExchangeCapabilitiesAsync(CancellationToken cancellation) =>
        OffThread(() => ExchangeCoreAsync(cancellation));

    public Task<AttemptOutcome<PeerDescription>> ConfigureStreamAsync(StreamSettings stream, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OffThread(() => ConfigureCoreAsync(stream, cancellation));
    }

    public Task<ConnectionEnd> WaitForEndAsync(CancellationToken cancellation)
    {
        Link link;
        lock (_gate)
        {
            link = _current is { WasConfigured: true } current
                ? current
                : throw new InvalidOperationException("There is no configured stream to wait for: ConfigureStreamAsync has not succeeded on the current connection.");
        }

        return link.Ended.WaitAsync(cancellation);
    }

    public void OfferVideo(ReadOnlySpan<byte> accessUnit, long captureMicros)
    {
        Interlocked.Increment(ref _offered);

        // Always looked at, so that the latest SPS and PPS are known whenever a client appears.
        VideoFlags flags = _parameterSets.Classify(accessUnit, out ReadOnlySpan<byte> parameterSets);

        if (_streaming is not { } link)
        {
            Interlocked.Increment(ref _notSentNoStream);
            return;
        }

        if (WireFormat.VideoHeaderLength + parameterSets.Length + accessUnit.Length > WireFormat.MaxVideoPayload)
        {
            // The frames after it depend on it: it uses up a sequence number, so that the client
            // sees the gap, and nothing more is sent until a keyframe (D64).
            link.SkipTooLarge();
            Interlocked.Increment(ref _droppedTooLarge);
            TraceKeyframeOffered(flags, captureMicros, "dropped_too_large");
            WantKeyframe(KeyframeRequestSource.TooLarge);
            return;
        }

        EnqueueResult result = link.Enqueue(flags, parameterSets, accessUnit, captureMicros);
        TraceKeyframeOffered(flags, captureMicros, result.Outcome switch
        {
            EnqueueOutcome.Queued => "queued",
            EnqueueOutcome.DroppedQueueFull => "dropped_queue_full",
            _ => "dropped_awaiting_keyframe",
        });
        switch (result.Outcome)
        {
            case EnqueueOutcome.DroppedAwaitingKeyframe:
                Interlocked.Increment(ref _droppedAwaitingKeyframe);
                break;
            case EnqueueOutcome.DroppedQueueFull:
                Interlocked.Add(ref _droppedQueueFull, 1 + result.AlsoDropped);

                // D64: the keyframe is asked for by the writer once it can send again, not now. The
                // queue is full because the writer is held up; a keyframe made now would wait behind
                // it, and asking now would spend the gate's interval on it, so that the keyframe the
                // client needs once the writer is free could wait up to 500 ms (D63).
                if (link.OweRecoveryKeyframe(MonotonicClock.NowMicros) is { } owedSince)
                {
                    Log(KeyframeRequestTrace.RecoveryOwed(owedSince, 1 + result.AlsoDropped, result.KeptKeyframe));
                }

                break;
            default:
                Interlocked.Add(ref _droppedQueueFull, result.AlsoDropped);
                break;
        }

        // A frame refused while the queue waits for a keyframe asks for nothing (D64): the keyframe
        // it waits for is already owed, to the stream's start (D27, asked by the coordinator) or to
        // the overflow that started the wait (asked by the writer), or was asked for when a frame
        // was too large.
    }

    /// <summary>
    /// The writer is free after a queue overflow (D64): asks for the keyframe the overflow made
    /// necessary, unless one has been queued since.
    /// </summary>
    private void RequestRecoveryKeyframe(long owedSinceMicros, bool stillAwaiting)
    {
        long now = MonotonicClock.NowMicros;
        Log(KeyframeRequestTrace.RecoveryWriterFree(now, owedSinceMicros, stillAwaiting));
        if (stillAwaiting)
        {
            WantKeyframe(KeyframeRequestSource.QueueFull);
        }
    }

    public Task CloseAsync(string reason, SessionFault? fault, CancellationToken cancellation) =>
        OffThread(async () =>
        {
            Link? link;
            lock (_gate)
            {
                _generation++;
                link = _current;
                _current = null;
            }

            if (link is not null)
            {
                // A cancelled close skips the last words, never the closing.
                object? lastWords = cancellation.IsCancellationRequested ? null
                    : fault is not null ? new ErrorMessage(WireCode(fault), fault.Message, true)
                    : new ByeMessage(reason);
                await link.ShutdownAsync(lastWords, ConnectionEndKind.Closed, null).ConfigureAwait(false);
            }

            await _transport.ReleaseAsync(cancellation).ConfigureAwait(false);
            return true;
        });

    // ---- Steps ---------------------------------------------------------------------------------

    private Task<AttemptOutcome> OpenAsyncCore(CancellationToken cancellation)
    {
        // Taken on the caller's thread, so that a close called after this open counts as after it,
        // even if it runs before the open's body does.
        int generation;
        Link? previous;
        lock (_gate)
        {
            generation = _generation;
            previous = _current;
            _current = null;
        }

        return OffThread(() => OpenCoreAsync(generation, previous, cancellation));
    }

    private async Task<AttemptOutcome> OpenCoreAsync(int generation, Link? previous, CancellationToken cancellation)
    {
        // A connection left over from an earlier attempt, or one that has ended, is discarded first.
        if (previous is not null)
        {
            await previous.ShutdownAsync(null, ConnectionEndKind.Closed, null).ConfigureAwait(false);
        }

        AttemptOutcome<TransportEndpoint> prepared = await _transport.PrepareAsync(cancellation).ConfigureAwait(false);
        if (prepared.Kind != OutcomeKind.Succeeded)
        {
            return prepared.Kind == OutcomeKind.Cancelled ? AttemptOutcome.Cancelled : AttemptOutcome.Failed(prepared.Fault!);
        }

        var socket = new Socket(prepared.Value!.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (cancellation.Register(socket.Dispose))
        {
            limit.CancelAfter(_options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(prepared.Value.EndPoint, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                socket.Dispose();
                if (cancellation.IsCancellationRequested)
                {
                    return AttemptOutcome.Cancelled;
                }

                return AttemptOutcome.Failed(exception is OperationCanceledException
                    ? Retryable(FaultCategory.Timeout, ConnectionFaultCodes.ConnectTimeout, $"Connecting to the forwarded port did not finish within {_options.ConnectTimeout.TotalSeconds:0.#} s.")
                    : exception is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                        ? Retryable(FaultCategory.Connection, ConnectionFaultCodes.ConnectionRefused, "Nothing accepted a connection on the forwarded port.")
                        : Retryable(FaultCategory.Connection, ConnectionFaultCodes.ConnectFailed, $"Connecting to the forwarded port failed: {Describe(exception)}"));
            }
        }

        // The route's own security (D75): nothing for USB, mutual TLS with the pin for Wi-Fi. Bounded
        // like a handshake step; cancelling closes the socket, which ends a TLS handshake in progress.
        Stream stream;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (limit.Token.Register(socket.Dispose))
        {
            limit.CancelAfter(_options.HandshakeStepTimeout);
            AttemptOutcome<Stream> secured;
            try
            {
                secured = await _transport.SecureAsync(socket, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                secured = cancellation.IsCancellationRequested
                    ? AttemptOutcome<Stream>.Cancelled
                    : AttemptOutcome<Stream>.Failed(Retryable(FaultCategory.Connection, ConnectionFaultCodes.ConnectFailed, $"Securing the connection failed: {Describe(exception)}"));
            }

            if (secured.Kind != OutcomeKind.Succeeded || cancellation.IsCancellationRequested)
            {
                secured.Value?.Dispose();
                socket.Dispose();
                if (cancellation.IsCancellationRequested || secured.Kind == OutcomeKind.Cancelled)
                {
                    return cancellation.IsCancellationRequested
                        ? AttemptOutcome.Cancelled
                        : AttemptOutcome.Failed(Retryable(FaultCategory.Timeout, ConnectionFaultCodes.SecureTimeout, $"Securing the connection did not finish within {_options.HandshakeStepTimeout.TotalSeconds:0.#} s."));
                }

                Log($"securing the connection failed: {secured.Fault!.Code}: {secured.Fault.Message}");
                return AttemptOutcome.Failed(secured.Fault!);
            }

            stream = secured.Value!;
        }

        var link = new Link(this, socket, stream);
        bool closedMeanwhile;
        lock (_gate)
        {
            closedMeanwhile = _generation != generation;
            if (!closedMeanwhile && !cancellation.IsCancellationRequested)
            {
                _current = link;
            }
        }

        if (closedMeanwhile || cancellation.IsCancellationRequested)
        {
            // Either the session was closed while this attempt was connecting (the attempt was
            // abandoned), or it was cancelled just as it connected. Nobody will use this socket.
            link.Abort();
            return cancellation.IsCancellationRequested
                ? AttemptOutcome.Cancelled
                : AttemptOutcome.Failed(Retryable(FaultCategory.Connection, ConnectionFaultCodes.ClosedByHost, "The session was closed while connecting."));
        }

        return AttemptOutcome.Succeeded;
    }

    private async Task<AttemptOutcome<ClientCapabilities>> ExchangeCoreAsync(CancellationToken cancellation)
    {
        Link link = CurrentFor(LinkStage.Connected, nameof(ExchangeCapabilitiesAsync));
        HelloMessage hello;
        CapabilitiesMessage capabilities;
        using (cancellation.Register(link.Abort))
        {
            try
            {
                await link.WritePrefaceAndHelloAsync(new HelloMessage(WireFormat.MajorVersion, WireFormat.MajorVersion, "host", _options.DeviceName, _options.AppVersion), cancellation).ConfigureAwait(false);

                // adb accepts the TCP connection whether or not the app is listening behind it.
                // Only the client's own preface and hello prove there is an ExtendSpace client there.
                hello = await link.ExpectAsync<HelloMessage>(HelloTimedOut, cancellation).ConfigureAwait(false);
                if (hello.ProtocolMin > WireFormat.MajorVersion || hello.ProtocolMax < WireFormat.MajorVersion)
                {
                    throw new ProtocolException(
                        WireErrorCode.ProtocolVersionUnsupported,
                        $"The device speaks protocol versions {hello.ProtocolMin} to {hello.ProtocolMax}; this PC speaks {WireFormat.MajorVersion}.");
                }

                if (hello.Role.Length > 0 && hello.Role != "client")
                {
                    throw new ProtocolException(WireErrorCode.UnexpectedMessage, $"The peer introduced itself as '{Printable(hello.Role)}', not as a client.");
                }

                capabilities = await link.ExpectAsync<CapabilitiesMessage>(
                    () => $"The device did not send its capabilities within {_options.HandshakeStepTimeout.TotalSeconds:0.#} s of its hello.",
                    cancellation).ConfigureAwait(false);
            }
            catch (PeerGoodbyeException goodbye) when (!cancellation.IsCancellationRequested)
            {
                await EndedByPeerAsync(link, goodbye.Reason).ConfigureAwait(false);
                return AttemptOutcome<ClientCapabilities>.PeerEnded(Printable(goodbye.Reason));
            }
            catch (Exception exception)
            {
                return await FailStepAsync(link, exception, cancellation).ConfigureAwait(false) is { } fault
                    ? AttemptOutcome<ClientCapabilities>.Failed(fault)
                    : AttemptOutcome<ClientCapabilities>.Cancelled;
            }
        }

        if (!link.Advance(LinkStage.Connected, LinkStage.Exchanged, hello) || cancellation.IsCancellationRequested)
        {
            // Completed just as it was cancelled, or as the session was closed: the connection is
            // gone or about to be, and success is not reported for it.
            Discard(link);
            return cancellation.IsCancellationRequested ? AttemptOutcome<ClientCapabilities>.Cancelled : AttemptOutcome<ClientCapabilities>.Failed(ClosedDuringStep());
        }

        return AttemptOutcome<ClientCapabilities>.Succeeded(new ClientCapabilities(
            hello.DeviceName,
            hello.AppVersion,
            capabilities.DisplayWidth,
            capabilities.DisplayHeight,
            capabilities.RefreshHz,
            [.. capabilities.Decoders.Select(decoder => new DecoderCapability(decoder.Name, decoder.Hardware, decoder.LowLatency, decoder.MaxWidth, decoder.MaxHeight))]));
    }

    private async Task<AttemptOutcome<PeerDescription>> ConfigureCoreAsync(StreamSettings stream, CancellationToken cancellation)
    {
        Link link = CurrentFor(LinkStage.Exchanged, nameof(ConfigureStreamAsync));
        var config = new StreamConfigMessage(
            "h264",
            _options.StreamProfile,
            stream.Width,
            stream.Height,
            stream.FrameRate,
            stream.BitrateBitsPerSecond,
            WireFormat.MaxVideoPayload,
            stream.EncoderName,
            stream.EncoderIsHardware ? "hardware" : "software",
            InputTouch: _options.Input?.Offered == true,
            Mode: _options.Extending?.Invoke() == true ? "extend" : null);

        StreamReadyMessage ready;
        using (cancellation.Register(link.Abort))
        {
            try
            {
                await link.WriteMessageAsync(config, _options.HandshakeStepTimeout, cancellation).ConfigureAwait(false);
                ready = await link.ExpectAsync<StreamReadyMessage>(
                    () => $"The device did not answer the stream configuration within {_options.HandshakeStepTimeout.TotalSeconds:0.#} s.",
                    cancellation).ConfigureAwait(false);
            }
            catch (PeerGoodbyeException goodbye) when (!cancellation.IsCancellationRequested)
            {
                await EndedByPeerAsync(link, goodbye.Reason).ConfigureAwait(false);
                return AttemptOutcome<PeerDescription>.PeerEnded(Printable(goodbye.Reason));
            }
            catch (Exception exception)
            {
                return await FailStepAsync(link, exception, cancellation).ConfigureAwait(false) is { } fault
                    ? AttemptOutcome<PeerDescription>.Failed(fault)
                    : AttemptOutcome<PeerDescription>.Cancelled;
            }

            // Started inside the registration, so that a cancellation from here on still closes
            // the socket the loops are using.
            if (!link.Advance(LinkStage.Exchanged, LinkStage.Streaming, null))
            {
                Discard(link);
                return cancellation.IsCancellationRequested ? AttemptOutcome<PeerDescription>.Cancelled : AttemptOutcome<PeerDescription>.Failed(ClosedDuringStep());
            }

            Interlocked.Increment(ref _streamsConfigured);
            Volatile.Write(ref _latestClientReport, null);
            _keyframeTrace.Reset();

            // D64: a request deferred on an earlier connection is not released on this one's writer,
            // and this stream's first request is not held by an earlier one's interval. The stream's
            // own first keyframe is the coordinator's to ask for (D27).
            _keyframes.Reset();
            if (config.InputTouch)
            {
                // Input is bound to this stream (D84): frames from any earlier connection are ignored from here on.
                link.InputOffered = true;
                _options.Input!.BeginStream(link, stream.Width, stream.Height);
            }

            link.StartStreaming();
            _streaming = link;
        }

        if (cancellation.IsCancellationRequested)
        {
            // Configured just as it was cancelled: the stream exists but nobody will use it.
            await link.ShutdownAsync(null, ConnectionEndKind.Closed, null).ConfigureAwait(false);
            Discard(link);
            return AttemptOutcome<PeerDescription>.Cancelled;
        }

        HelloMessage hello = link.ClientHello!;
        Log($"streaming to '{Printable(hello.DeviceName)}' (app {Printable(hello.AppVersion)}); the client will decode with {(ready.Decoder is { } decoder ? Printable(decoder) : "an unnamed decoder")}");
        return AttemptOutcome<PeerDescription>.Succeeded(new PeerDescription(hello.DeviceName, hello.AppVersion, ready.Decoder, ready.Hardware));
    }

    private string HelloTimedOut() =>
        $"Nothing answered within {_options.HandshakeStepTimeout.TotalSeconds:0.#} s of connecting. The app may not be open, or it may be serving another PC: it serves one at a time.";

    /// <summary>
    /// Classifies why a handshake step failed, tells the client where the protocol asks for it, and
    /// discards the connection. Null when the step was cancelled.
    /// </summary>
    private async Task<SessionFault?> FailStepAsync(Link link, Exception exception, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            Discard(link);
            return null;
        }

        SessionFault fault;
        object? tell = null;
        switch (exception)
        {
            case StepTimeoutException timeout:
                // Protocol section 7: the side that is waiting sends a fatal timeout error and closes.
                fault = Retryable(FaultCategory.Timeout, ConnectionFaultCodes.HandshakeTimeout, timeout.Message);
                tell = new ErrorMessage(WireErrorCode.Timeout, timeout.Message, true);
                break;
            case ProtocolException violation:
                fault = new SessionFault(FaultCategory.Protocol, violation.Code, violation.Message, FaultTreatment.Terminal);

                // Protocol section 4: a peer whose preface is wrong is disconnected without a reply;
                // it is not an ExtendSpace client and would not understand one.
                tell = link.PeerPrefaceReceived ? new ErrorMessage(violation.Code, violation.Message, true) : null;
                break;
            case PeerErrorException peer:
                fault = PeerErrorFault(peer.Error);
                break;
            case EndOfStreamException ended:
                fault = Retryable(FaultCategory.Connection, link.BytesReceived == 0 ? ConnectionFaultCodes.NoAppListening : ConnectionFaultCodes.ConnectionClosed, ended.Message);
                break;
            case SocketException or ObjectDisposedException or IOException:
                fault = link.IsAborted && !link.AbortedByFault
                    ? ClosedDuringStep()
                    : Retryable(FaultCategory.Connection, ConnectionFaultCodes.ConnectionReset, $"The connection failed during the handshake: {Describe(exception)}");
                break;
            default:
                fault = SessionFault.Unexpected(exception);
                tell = new ErrorMessage(WireErrorCode.InternalError, fault.Message, true);
                break;
        }

        // Forgotten first, then closed after the last words, if there are any.
        Forget(link);
        await link.ShutdownAsync(tell, ConnectionEndKind.Closed, null).ConfigureAwait(false);
        Log($"handshake failed: {fault.Code}: {fault.Message}");
        return fault;
    }

    /// <summary>
    /// The client said <c>bye</c> during a handshake step (decision D44). Protocol section 7: the
    /// receiver stops immediately and does not reconnect, and nothing is said in reply. The
    /// connection is closed; the transport stays the session's until <see cref="CloseAsync"/>.
    /// </summary>
    private async Task EndedByPeerAsync(Link link, string reason)
    {
        Forget(link);
        await link.ShutdownAsync(null, ConnectionEndKind.PeerEnded, new ConnectionEnd(ConnectionEndKind.PeerEnded, PeerReason: Printable(reason))).ConfigureAwait(false);
        Log($"the client ended the session during the handshake: {Printable(reason)}");
    }

    // ---- Shared ----------------------------------------------------------------------------------

    private Link CurrentFor(LinkStage stage, string call)
    {
        lock (_gate)
        {
            return _current is { } link && link.Stage == stage
                ? link
                : throw new InvalidOperationException($"{call} needs a connection that has reached {stage}; the steps of an attempt are Open, Exchange, Configure, in that order.");
        }
    }

    /// <summary>Forgets <paramref name="link"/> if it is the current connection, and closes its socket.</summary>
    private void Discard(Link link)
    {
        Forget(link);
        link.Abort();
    }

    private void Forget(Link link)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, link))
            {
                _current = null;
            }
        }

        if (ReferenceEquals(_streaming, link))
        {
            _streaming = null;
        }
    }

    private void WantKeyframe(KeyframeRequestSource source)
    {
        long now = MonotonicClock.NowMicros;
        KeyframeGateResult result = _keyframes.Request(now, source);
        if (_options.Log is not null && KeyframeRequestTrace.GateDecision(now, source, result) is { } line)
        {
            Log(line);
        }

        if (result.Passed)
        {
            RaiseKeyframeWanted(now);
        }
    }

    /// <summary>A deferred request the writer found due: logged, then passed on.</summary>
    private void ReleaseDeferredKeyframe(KeyframeGatePending released)
    {
        long now = MonotonicClock.NowMicros;
        Log(KeyframeRequestTrace.Released(now, released));
        RaiseKeyframeWanted(now);
    }

    private void RaiseKeyframeWanted(long nowMicros)
    {
        _keyframeTrace.Raised(nowMicros);
        Interlocked.Increment(ref _keyframesWanted);
        KeyframeWanted?.Invoke();
    }

    // Only keyframes reach the trace's lock; other frames cost a flag test.
    private void TraceKeyframeOffered(VideoFlags flags, long captureMicros, string sendOutcome)
    {
        if (flags.HasFlag(VideoFlags.Keyframe) && _keyframeTrace.KeyframeOffered(MonotonicClock.NowMicros, captureMicros, sendOutcome) is { } line)
        {
            Log(line);
        }
    }

    private void Log(string line) => _options.Log?.Invoke(line);

    /// <summary>The fault a client's <c>error</c> message amounts to.</summary>
    private static SessionFault PeerErrorFault(ErrorMessage error)
    {
        string message = $"The device reported {Printable(error.Code)}: {Printable(error.Message)}";
        return error.Code switch
        {
            // The client gave up waiting for this side. It goes back to listening; worth another attempt.
            WireErrorCode.Timeout => Retryable(FaultCategory.Timeout, ConnectionFaultCodes.PeerTimeout, message),

            // Serving another PC. Equally worth another attempt within the budget.
            WireErrorCode.Busy => Retryable(FaultCategory.Connection, ConnectionFaultCodes.PeerBusy, message),

            // The code is kept when the protocol defines it, so that a rejected stream maps to
            // NegotiationFailed (architecture section 9.3).
            _ => new SessionFault(FaultCategory.Protocol, WireErrorCode.All.Contains(error.Code) ? error.Code : ConnectionFaultCodes.PeerError, message, FaultTreatment.Terminal),
        };
    }

    /// <summary>The protocol error code a session fault is told to the client as (protocol section 12).</summary>
    internal static string WireCode(SessionFault fault) => fault.Category switch
    {
        FaultCategory.Protocol when WireErrorCode.All.Contains(fault.Code) => fault.Code,
        FaultCategory.Timeout => WireErrorCode.Timeout,
        _ => WireErrorCode.InternalError,
    };

    private static SessionFault ClosedDuringStep() =>
        Retryable(FaultCategory.Connection, ConnectionFaultCodes.ClosedByHost, "The session was closed during the handshake.");

    private static SessionFault Retryable(FaultCategory category, string code, string message) =>
        new(category, code, message, FaultTreatment.Retryable);

    /// <summary>A short, printable excerpt of text the peer sent, for a log line or a fault's message.</summary>
    internal static string Printable(string text, int max = 160)
    {
        string excerpt = text.Length > max ? text[..max] + "…" : text;
        return string.Create(excerpt.Length, excerpt, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private static string Describe(Exception exception) => exception switch
    {
        SocketException socket => $"{socket.SocketErrorCode}: {socket.Message}",
        { InnerException: SocketException inner } => $"{inner.SocketErrorCode}: {inner.Message}",
        _ => exception.Message,
    };

    private static Task<T> OffThread<T>(Func<Task<T>> body)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(body).ContinueWith(
            static (finished, state) =>
            {
                var target = (TaskCompletionSource<T>)state!;
                if (finished.IsCanceled)
                {
                    target.TrySetCanceled();
                }
                else if (finished.IsFaulted)
                {
                    target.TrySetException(finished.Exception!.InnerExceptions);
                }
                else
                {
                    target.TrySetResult(finished.Result);
                }
            },
            done,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return done.Task;
    }

    private static Task OffThread(Func<Task<bool>> body) => OffThread<bool>(body);

    // ---- One connection --------------------------------------------------------------------------

    private enum LinkStage
    {
        Connected,
        Exchanged,
        Streaming,
        Ended,
    }

    /// <summary>The handshake step did not complete in time.</summary>
    private sealed class StepTimeoutException(string message) : Exception(message);

    /// <summary>The client sent <c>error</c> where a handshake message was expected.</summary>
    private sealed class PeerErrorException(ErrorMessage error) : Exception(error.Message)
    {
        public ErrorMessage Error { get; } = error;
    }

    /// <summary>The client said <c>bye</c> during the handshake.</summary>
    private sealed class PeerGoodbyeException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    private readonly record struct ReceivedFrame(FrameHeader Header, byte[] Payload, long ReceivedMicros);

    /// <summary>
    /// One socket's life, from connected to closed. Its writes are serialised by one lock: the
    /// handshake's, the writer loop's, and the last message's. Its reads are made by one caller at
    /// a time: a handshake step, then the reader loop.
    /// </summary>
    private sealed class Link(HostConnectionController owner, Socket socket, Stream stream)
    {
        private readonly HostConnectionOptions _options = owner._options;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly FrameParser _parser = new();
        private readonly Queue<ReceivedFrame> _frames = new();
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly CancellationTokenSource _stopWriting = new();
        private readonly TaskCompletionSource<ConnectionEnd> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _shutDown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<long, long> _pingsInFlight = new();
        private readonly VideoSendQueue _queue = new(owner._options.SendQueueCapacity);
        private readonly Lock _stageGate = new();

        private LinkStage _stage = LinkStage.Connected;
        private int _aborted;
        private int _shuttingDown;
        private volatile bool _closing;
        private bool _prefaceSent;
        private uint _sequence;

        // D64: when an overflow's keyframe became owed, on the monotonic clock; 0 when none is owed.
        // Set by the pipeline's thread, taken by the writer.
        private long _recoveryOwedSince;
        private Task? _reader;
        private Task? _writer;

        public ClockOffsetEstimator Clock { get; } = new();

        public Task<ConnectionEnd> Ended => _ended.Task;

        public HelloMessage? ClientHello { get; private set; }

        public long BytesReceived { get; private set; }

        public bool PeerPrefaceReceived => _parser.PrefaceReceived;

        public bool IsAborted => Volatile.Read(ref _aborted) != 0;

        /// <summary>The socket was closed because the connection itself failed, not because someone closed it.</summary>
        public bool AbortedByFault { get; private set; }

        public LinkStage Stage
        {
            get
            {
                lock (_stageGate)
                {
                    return _stage;
                }
            }
        }

        public bool WasConfigured => _reader is not null;

        /// <summary>Whether this connection's <c>stream_config</c> offered input; only then may the client send it.</summary>
        public bool InputOffered { get; set; }

        /// <summary>Moves on to <paramref name="to"/> if still at <paramref name="from"/> and open.</summary>
        public bool Advance(LinkStage from, LinkStage to, HelloMessage? hello)
        {
            lock (_stageGate)
            {
                if (_stage != from || IsAborted)
                {
                    return false;
                }

                _stage = to;
                ClientHello = hello ?? ClientHello;
                return true;
            }
        }

        /// <summary>Closes the socket at once. Ends every receive, send and connect waiting on it. Idempotent.</summary>
        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) == 0)
            {
                lock (_stageGate)
                {
                    _stage = LinkStage.Ended;
                }

                // The socket first: closing it ends every read and write waiting on the stream.
                socket.Dispose();
                try
                {
                    stream.Dispose();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // A TLS stream whose socket is gone may complain while letting go; nothing is left to release.
                }
            }
        }

        // ---- Handshake I/O ----

        public async Task WritePrefaceAndHelloAsync(HelloMessage hello, CancellationToken cancellation)
        {
            byte[] hi = ControlCodec.Encode(hello);
            byte[] bytes = new byte[WireFormat.Preface.Length + hi.Length];
            WireFormat.Preface.CopyTo(bytes);
            hi.CopyTo(bytes.AsSpan(WireFormat.Preface.Length));
            await WriteAsync(bytes, _options.HandshakeStepTimeout, cancellation, markPreface: true).ConfigureAwait(false);
        }

        public Task WriteMessageAsync(object message, TimeSpan timeout, CancellationToken cancellation) =>
            WriteAsync(ControlCodec.Encode(message), timeout, cancellation, markPreface: false);

        /// <summary>
        /// The next handshake message, which must be a <typeparamref name="T"/>, within one handshake
        /// step. Control types this build does not know are skipped, as protocol section 5 requires.
        /// </summary>
        public async Task<T> ExpectAsync<T>(Func<string> timedOut, CancellationToken cancellation)
            where T : class
        {
            long deadline = MonotonicClock.NowMicros + (long)_options.HandshakeStepTimeout.TotalMicroseconds;
            while (true)
            {
                ReceivedFrame frame = await ReadFrameAsync(deadline, timedOut, cancellation).ConfigureAwait(false);
                if (frame.Header.Channel != WireFormat.ChannelControl)
                {
                    throw new ProtocolException(WireErrorCode.UnexpectedMessage, $"Expected a control message, received a frame on channel {frame.Header.Channel}.");
                }

                switch (ControlCodec.Decode(frame.Header.Type, frame.Payload))
                {
                    case T expected:
                        return expected;
                    case null:
                        continue;
                    case ErrorMessage error:
                        throw new PeerErrorException(error);
                    case ByeMessage bye:
                        throw new PeerGoodbyeException(bye.Reason);
                    default:
                        throw new ProtocolException(
                            WireErrorCode.UnexpectedMessage,
                            $"Expected the client's {Name(typeof(T))}, received {(ControlType)frame.Header.Type}.");
                }
            }
        }

        private static string Name(Type type) => type.Name.Replace("Message", string.Empty, StringComparison.Ordinal);

        // ---- Streaming ----

        public void StartStreaming()
        {
            // A new connection starts a new stream: sequence numbers from zero, and nothing is sent
            // before a keyframe that carries its parameter sets. The keyframe itself is the
            // coordinator's to ask for (decision D27).
            _sequence = 0;
            _reader = Task.Run(ReadLoopAsync);
            _writer = Task.Run(WriteLoopAsync);
        }

        /// <summary>Numbers and queues one access unit. Called on the pipeline's thread only.</summary>
        public EnqueueResult Enqueue(VideoFlags flags, ReadOnlySpan<byte> parameterSets, ReadOnlySpan<byte> accessUnit, long captureMicros)
        {
            uint sequence = _sequence++;
            return _queue.Enqueue(OutgoingVideoFrame.Create(sequence, captureMicros, flags, parameterSets, accessUnit));
        }

        /// <summary>A frame dropped before the queue: its sequence number is used up and the queue waits for a keyframe.</summary>
        public void SkipTooLarge()
        {
            _sequence++;
            _queue.AwaitKeyframe();
        }

        /// <summary>
        /// Records that an overflow needs a keyframe, to be asked for by the writer when it is next
        /// free. Returns when the obligation began if this call opened it; null if one was already open.
        /// Called on the pipeline's thread.
        /// </summary>
        public long? OweRecoveryKeyframe(long nowMicros)
        {
            long since = Math.Max(1, nowMicros);
            return Interlocked.CompareExchange(ref _recoveryOwedSince, since, 0) == 0 ? since : null;
        }

        // On the writer, at the top of its loop, so only when no write is in progress: the socket has
        // taken everything given to it so far.
        private void AskForOwedRecovery()
        {
            if (Volatile.Read(ref _recoveryOwedSince) == 0)
            {
                return;
            }

            long since = Interlocked.Exchange(ref _recoveryOwedSince, 0);
            if (since != 0)
            {
                owner.RequestRecoveryKeyframe(since, _queue.AwaitingKeyframe);
            }
        }

        private async Task ReadLoopAsync()
        {
            ConnectionEnd? end = null;
            object? lastWords = null;
            try
            {
                while (end is null)
                {
                    ReceivedFrame frame = await ReadFrameAsync(
                        MonotonicClock.NowMicros + (long)_options.KeepaliveTimeout.TotalMicroseconds,
                        () => $"Nothing was received from the device for {_options.KeepaliveTimeout.TotalSeconds:0.#} s.",
                        CancellationToken.None).ConfigureAwait(false);
                    end = Handle(frame);
                }
            }
            catch (StepTimeoutException silence)
            {
                end = new ConnectionEnd(ConnectionEndKind.Lost, Retryable(FaultCategory.Timeout, ConnectionFaultCodes.KeepaliveTimeout, silence.Message));
            }
            catch (ProtocolException violation)
            {
                end = new ConnectionEnd(ConnectionEndKind.Fatal, new SessionFault(FaultCategory.Protocol, violation.Code, violation.Message, FaultTreatment.Terminal));
                lastWords = new ErrorMessage(violation.Code, violation.Message, true);
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or SocketException or ObjectDisposedException)
            {
                end = _closing || (IsAborted && !AbortedByFault)
                    ? new ConnectionEnd(ConnectionEndKind.Closed)
                    : new ConnectionEnd(ConnectionEndKind.Lost, Retryable(
                        FaultCategory.Connection,
                        exception is EndOfStreamException ? ConnectionFaultCodes.ConnectionClosed : ConnectionFaultCodes.ConnectionReset,
                        exception is EndOfStreamException ? exception.Message : $"The connection failed: {Describe(exception)}"));
            }
            catch (Exception exception)
            {
                end = new ConnectionEnd(ConnectionEndKind.Fatal, SessionFault.Unexpected(exception));
                lastWords = new ErrorMessage(WireErrorCode.InternalError, end.Fault!.Message, true);
            }

            if (_closing)
            {
                // Someone else is closing this connection and will record the end; the reader only
                // had to see the client close after the last message.
                return;
            }

            if (end.Kind is ConnectionEndKind.Lost)
            {
                owner.Log($"connection lost: {end.Fault!.Code}: {end.Fault.Message}");
            }

            // Recorded before the shutdown, so that a write failing because of the shutdown cannot
            // be taken for the cause. A cause the writer recorded first stands.
            _ended.TrySetResult(end);
            await ShutdownAsync(lastWords, end.Kind, end, fromReader: true).ConfigureAwait(false);
        }

        /// <summary>What a frame from the client means while streaming; non-null when it ends the connection.</summary>
        private ConnectionEnd? Handle(ReceivedFrame frame)
        {
            if (frame.Header.Channel == WireFormat.ChannelInput && InputOffered)
            {
                // D81: touch frames on a stream that offered input; other input types are from a
                // later version and are ignored. A malformed frame ends the connection.
                if (frame.Header.Type == Input.TouchFrame.MessageType)
                {
                    owner._options.Input!.Handle(this, Input.TouchFrame.Parse(frame.Payload));
                }

                return null;
            }

            if (frame.Header.Channel != WireFormat.ChannelControl)
            {
                throw new ProtocolException(WireErrorCode.UnexpectedMessage, $"The client sent a frame on channel {frame.Header.Channel}, which it was not offered.");
            }

            switch (ControlCodec.Decode(frame.Header.Type, frame.Payload))
            {
                case PongMessage pong:
                    if (_pingsInFlight.TryRemove(pong.Id, out long sentMicros))
                    {
                        Clock.Add(sentMicros, frame.ReceivedMicros, pong.ClientTimeMicros);
                    }

                    return null;
                case StatsMessage stats:
                    // Validated, kept as the latest report for diagnostics and acceptance runs, and,
                    // like any message, proof that the client is alive. Acting on them is stage 6.
                    Volatile.Write(ref owner._latestClientReport, ClientReport.From(stats, frame.ReceivedMicros));
                    return null;
                case KeyframeRequestMessage request:
                    // Logged at frame.ReceivedMicros, when its read completed; the gate still reads
                    // the clock itself, as it always has, a few microseconds later.
                    owner.Log(KeyframeRequestTrace.ClientRequest(frame.ReceivedMicros, Printable(request.Reason, 40)));
                    owner.WantKeyframe(KeyframeRequestSource.Client);
                    return null;
                case ErrorMessage { Fatal: false } error:
                    owner.Log($"the client reported an error it carries on from: {Printable(error.Code)}: {Printable(error.Message)}");
                    return null;
                case ErrorMessage error:
                    SessionFault fault = PeerErrorFault(error);
                    return new ConnectionEnd(fault.IsRetryable ? ConnectionEndKind.Lost : ConnectionEndKind.Fatal, fault);
                case ByeMessage bye:
                    return new ConnectionEnd(ConnectionEndKind.PeerEnded, PeerReason: Printable(bye.Reason));
                case null:
                    // A control type from a later protocol version: ignored, as the protocol requires.
                    return null;
                default:
                    throw new ProtocolException(WireErrorCode.UnexpectedMessage, $"The client sent {(ControlType)frame.Header.Type}, which a client does not send while streaming.");
            }
        }

        private async Task WriteLoopAsync()
        {
            long pingId = 0;
            long nextPingMicros = MonotonicClock.NowMicros;
            ConnectionEnd? end = null;
            try
            {
                while (!_stopWriting.IsCancellationRequested)
                {
                    AskForOwedRecovery();

                    long now = MonotonicClock.NowMicros;
                    if (now >= nextPingMicros)
                    {
                        ClockOffset? clock = Clock.Best;
                        long id = ++pingId;

                        // The send time is taken as late as possible, right before the write.
                        long sendMicros = MonotonicClock.NowMicros;
                        _pingsInFlight[id] = sendMicros;
                        await WriteAsync(ControlCodec.Encode(new PingMessage(id, sendMicros, clock?.OffsetMicros, clock?.RoundTripMicros)), _options.KeepaliveTimeout, CancellationToken.None, markPreface: false).ConfigureAwait(false);

                        // A few quick pings first, so that a clock estimate exists within the first half second.
                        TimeSpan interval = id <= FastPings ? FastPingInterval : _options.KeepaliveInterval;
                        nextPingMicros = sendMicros + (long)interval.TotalMicroseconds;
                        ForgetOldPings(id);
                    }

                    if (owner._keyframes.TakeDue(now, out KeyframeGatePending released))
                    {
                        owner.ReleaseDeferredKeyframe(released);
                    }

                    long wakeMicros = Math.Min(nextPingMicros, owner._keyframes.DeferredDueMicros ?? long.MaxValue);
                    TimeSpan wait = TimeSpan.FromMicroseconds(Math.Max(0, wakeMicros - MonotonicClock.NowMicros));
                    if (await _queue.TryDequeueAsync(wait, _stopWriting.Token).ConfigureAwait(false) is not { } frame)
                    {
                        continue;
                    }

                    try
                    {
                        await WriteAsync(frame.Memory, _options.KeepaliveTimeout, CancellationToken.None, markPreface: false).ConfigureAwait(false);
                        Interlocked.Increment(ref owner._transmitted);
                        if (frame.Flags.HasFlag(VideoFlags.Keyframe))
                        {
                            Interlocked.Increment(ref owner._keyframesTransmitted);
                            if (owner._keyframeTrace.KeyframeSent(MonotonicClock.NowMicros) is { } sent)
                            {
                                owner.Log(sent);
                            }
                        }
                    }
                    catch
                    {
                        // Taken from the queue but never fully written: it was lost with the connection.
                        Interlocked.Increment(ref owner._discardedAtDisconnect);
                        throw;
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_stopWriting.IsCancellationRequested)
            {
                return;
            }
            catch (StepTimeoutException blocked)
            {
                // A client that stopped reading: the write could not finish within the keepalive time.
                end = new ConnectionEnd(ConnectionEndKind.Lost, Retryable(FaultCategory.Timeout, ConnectionFaultCodes.SendTimeout, blocked.Message));
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                end = new ConnectionEnd(ConnectionEndKind.Lost, Retryable(FaultCategory.Connection, ConnectionFaultCodes.ConnectionReset, $"Sending to the device failed: {Describe(exception)}"));
            }
            catch (Exception exception)
            {
                end = new ConnectionEnd(ConnectionEndKind.Fatal, SessionFault.Unexpected(exception));
            }

            // A write that fails once the connection is being ended for another reason is a
            // consequence of that, not a cause; the reason already recorded stands.
            if (end is not null && !_stopWriting.IsCancellationRequested)
            {
                owner.Log($"connection ended by the writer: {end.Fault!.Code}: {end.Fault.Message}");

                // The end is recorded before the socket is closed, so that the reader, which the
                // closing ends, does not report it as something else.
                AbortedByFault = true;
                _ended.TrySetResult(end);
                Abort();
            }
        }

        private void ForgetOldPings(long latestId)
        {
            foreach (long id in _pingsInFlight.Keys)
            {
                if (id < latestId - 10)
                {
                    _pingsInFlight.TryRemove(id, out _);
                }
            }
        }

        /// <summary>
        /// Ends the connection, once; a second caller waits for the first. The writer is given
        /// <see cref="HostConnectionOptions.WriterExitWait"/> to let go of the socket; if it did,
        /// <paramref name="lastWords"/> are sent within <see cref="HostConnectionOptions.FinalMessageTimeout"/>
        /// and the client is given as long to close in reply. Then the socket is closed, which is what
        /// ends a blocked write, and the loops are joined within <see cref="HostConnectionOptions.LoopJoinTimeout"/>.
        /// </summary>
        public async Task ShutdownAsync(object? lastWords, ConnectionEndKind kind, ConnectionEnd? end, bool fromReader = false)
        {
            if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
            {
                await _shutDown.Task.ConfigureAwait(false);
                return;
            }

            _closing = !fromReader;
            if (ReferenceEquals(owner._streaming, this))
            {
                owner._streaming = null;
            }

            // Every contact and button this connection's touches hold is released, whatever the reason (D84).
            if (InputOffered)
            {
                owner._options.Input!.EndStream(this);
            }

            try
            {
                _stopWriting.Cancel();
                bool writerLetGo = _writer is null || await Within(_writer, _options.WriterExitWait).ConfigureAwait(false);

                if (lastWords is not null && writerLetGo && _prefaceSent && !IsAborted)
                {
                    if (await TrySendLastAsync(lastWords).ConfigureAwait(false) && !fromReader && _reader is not null)
                    {
                        // The client closes when it reads a bye or a fatal error. Waiting for that
                        // (the reader sees the end of the stream) lets the message arrive before
                        // the socket is closed: closing with unread data in it would reset the
                        // connection, and a reset can overtake the message.
                        await Within(_reader, _options.FinalMessageTimeout).ConfigureAwait(false);
                    }
                }
                else if (lastWords is not null && !writerLetGo)
                {
                    owner.Log("the connection was closed without a final message: a write to the client was blocked");
                }

                Abort();

                // Closing the socket completes every receive and send waiting on it, so the loops
                // end at once. If one has not, it is reported rather than waited for.
                Task loops = Task.WhenAll(fromReader ? [_writer ?? Task.CompletedTask] : [_reader ?? Task.CompletedTask, _writer ?? Task.CompletedTask]);
                if (!await Within(loops, _options.LoopJoinTimeout).ConfigureAwait(false))
                {
                    owner.Log($"the connection's reader or writer had not finished {_options.LoopJoinTimeout.TotalSeconds:0.#} s after its socket was closed");
                }

                Interlocked.Add(ref owner._discardedAtDisconnect, _queue.Reset());
            }
            finally
            {
                _ended.TrySetResult(end ?? new ConnectionEnd(kind));
                _shutDown.TrySetResult();
            }
        }

        private async Task<bool> TrySendLastAsync(object message)
        {
            try
            {
                await WriteAsync(ControlCodec.Encode(message), _options.FinalMessageTimeout, CancellationToken.None, markPreface: false).ConfigureAwait(false);
                if (stream is System.Net.Security.SslStream tls)
                {
                    // TLS says goodbye itself (close_notify), so that the peer can tell an orderly end
                    // from a truncation; bounded like the message it follows.
                    await tls.ShutdownAsync().WaitAsync(_options.FinalMessageTimeout).ConfigureAwait(false);
                }

                socket.Shutdown(SocketShutdown.Send);
                return true;
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or StepTimeoutException or TimeoutException or InvalidOperationException)
            {
                owner.Log($"could not send the final {message.GetType().Name.Replace("Message", string.Empty, StringComparison.Ordinal)}: {exception.Message}");
                return false;
            }
        }

        // ---- Socket primitives ----

        /// <summary>
        /// Writes all of <paramref name="bytes"/> under the write lock. A write that does not finish
        /// within <paramref name="timeout"/>, or is cancelled, leaves the stream unusable, so the
        /// socket is closed and <see cref="StepTimeoutException"/> or the cancellation is thrown.
        /// </summary>
        private async Task WriteAsync(ReadOnlyMemory<byte> bytes, TimeSpan timeout, CancellationToken cancellation, bool markPreface)
        {
            if (!await _writeLock.WaitAsync(timeout, cancellation).ConfigureAwait(false))
            {
                throw new StepTimeoutException($"Another write held the connection for {timeout.TotalSeconds:0.#} s.");
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                limit.CancelAfter(timeout);
                try
                {
                    await stream.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
                    if (markPreface)
                    {
                        _prefaceSent = true;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    AbortedByFault = true;
                    Abort();
                    throw new StepTimeoutException($"A write to the device did not finish within {timeout.TotalSeconds:0.#} s.");
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// The next frame, by <paramref name="deadlineMicros"/>. A receive that runs out of time is
        /// cancelled (the socket stays open, so that the caller can still say why it is giving up)
        /// and <see cref="StepTimeoutException"/> is thrown. The end of the stream is
        /// <see cref="EndOfStreamException"/>; bytes that are not the protocol are a
        /// <see cref="ProtocolException"/>.
        /// </summary>
        private async Task<ReceivedFrame> ReadFrameAsync(long deadlineMicros, Func<string> timedOut, CancellationToken cancellation)
        {
            while (_frames.Count == 0)
            {
                long remaining = deadlineMicros - MonotonicClock.NowMicros;
                if (remaining <= 0)
                {
                    throw new StepTimeoutException(timedOut());
                }

                int read;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    limit.CancelAfter(TimeSpan.FromMicroseconds(remaining));
                    try
                    {
                        read = await stream.ReadAsync(_buffer, limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        throw new StepTimeoutException(timedOut());
                    }
                }

                if (read == 0)
                {
                    throw new EndOfStreamException(BytesReceived == 0
                        ? "The connection was closed before anything arrived. Behind an adb port forward that means no app is listening on the device."
                        : _parser.IsAtFrameBoundary
                            ? "The device closed the connection."
                            : "The device closed the connection in the middle of a message.");
                }

                BytesReceived += read;
                long receivedMicros = MonotonicClock.NowMicros;
                _parser.Append(_buffer.AsSpan(0, read), (header, payload) => _frames.Enqueue(new ReceivedFrame(header, payload.ToArray(), receivedMicros)));
            }

            return _frames.Dequeue();
        }

        private static async Task<bool> Within(Task task, TimeSpan bound)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            Task first = await Task.WhenAny(task, Task.Delay(bound)).ConfigureAwait(false);
            return first == task;
        }
    }
}

/// <summary>What the client said in its latest <c>stats</c> message: cumulative counts since its stream began.</summary>
/// <param name="RenderLatencyMeanMs">Capture on the host to release to the surface on the client, mean over the client's last interval; null when it could not relate the clocks.</param>
/// <param name="ReceivedMicros">When it arrived, on the host's monotonic clock.</param>
public sealed record ClientReport(
    long FramesReceived,
    long FramesMissing,
    long FramesDiscarded,
    long FramesSubmitted,
    long FramesDecoded,
    long FramesRendered,
    long DecoderErrors,
    double? RenderLatencyMeanMs,
    double? RenderLatencyP95Ms,
    long ReceivedMicros)
{
    internal static ClientReport From(StatsMessage stats, long receivedMicros) => new(
        stats.FramesReceived,
        stats.FramesMissing,
        stats.FramesDiscarded,
        stats.FramesSubmitted,
        stats.FramesDecoded,
        stats.FramesRendered,
        stats.DecoderErrors,
        stats.RenderLatency?.MeanMs,
        stats.RenderLatency?.P95Ms,
        receivedMicros);
}

/// <summary>Fault codes the connection controller raises.</summary>
public static class ConnectionFaultCodes
{
    /// <summary>Connection: adb accepted and closed at once; nothing is listening on the device.</summary>
    public const string NoAppListening = "no_app_listening";

    public const string ConnectionRefused = "connection_refused";
    public const string ConnectFailed = "connect_failed";
    public const string ConnectionClosed = "connection_closed";
    public const string ConnectionReset = "connection_reset";

    /// <summary>Connection: this side closed the session while the step was running.</summary>
    public const string ClosedByHost = "closed_by_host";

    public const string PeerBusy = "peer_busy";

    /// <summary>Protocol, terminal: the client sent a fatal error whose code the protocol does not define.</summary>
    public const string PeerError = "peer_error";

    public const string ConnectTimeout = "connect_timeout";

    /// <summary>Timeout: the route's security step (TLS over Wi-Fi) did not finish in time.</summary>
    public const string SecureTimeout = "secure_timeout";
    public const string HandshakeTimeout = "handshake_timeout";
    public const string KeepaliveTimeout = "keepalive_timeout";
    public const string SendTimeout = "send_timeout";
    public const string PeerTimeout = "peer_timeout";
}
