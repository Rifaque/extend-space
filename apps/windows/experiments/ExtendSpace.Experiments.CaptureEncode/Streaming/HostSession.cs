using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>The session states of protocol section 7, as the host goes through them.</summary>
internal enum SessionPhase
{
    Idle,
    Connecting,
    Negotiating,
    Streaming,
    Reconnecting,

    /// <summary>Ended in an orderly way: by this side, or by the client saying goodbye.</summary>
    Disconnected,

    /// <summary>Ended by an error, or because a lost connection could not be re-established in time.</summary>
    Failed,
}

/// <summary>
/// Where each encoded frame went. Every frame the encoder produces is counted in exactly one of the
/// outcome fields, so <see cref="Unaccounted"/> is the number still queued for the socket.
/// </summary>
internal sealed record HostStreamCounters
{
    /// <summary>Access units received from the encoder.</summary>
    public long Encoded { get; init; }

    public long EncodedBytes { get; init; }

    /// <summary>Encoded while no client was in the Streaming state; never queued.</summary>
    public long NotSentNoClient { get; init; }

    /// <summary>Not queued because the client cannot decode anything before the next keyframe.</summary>
    public long DroppedAwaitingKeyframe { get; init; }

    /// <summary>Thrown away, queued or not, because the socket could not take frames fast enough.</summary>
    public long DroppedQueueFull { get; init; }

    /// <summary>Still queued when the connection ended.</summary>
    public long DiscardedAtDisconnect { get; init; }

    /// <summary>Larger than the protocol allows a video frame to be.</summary>
    public long DroppedTooLarge { get; init; }

    /// <summary>Written to the socket in full.</summary>
    public long Transmitted { get; init; }

    /// <summary>Bytes written to the socket for video, headers included.</summary>
    public long TransmittedBytes { get; init; }

    public long KeyframesTransmitted { get; init; }

    /// <summary>Times the encoder was asked for a keyframe, by the client or by this side.</summary>
    public long KeyframeRequests { get; init; }

    public long ConnectionsEstablished { get; init; }

    public long ConnectionsLost { get; init; }

    public long Unaccounted =>
        Encoded - NotSentNoClient - DroppedAwaitingKeyframe - DroppedQueueFull - DiscardedAtDisconnect - DroppedTooLarge - Transmitted;
}

internal sealed record HostSessionOptions(
    int Port,
    TimeSpan ReconnectBudget,
    int SendQueueCapacity,
    string DeviceName,
    string AppVersion);

/// <summary>
/// The host's end of one ExtendSpace session over a loopback TCP port that adb forwards to the
/// Android app. It opens the connection, runs the handshake, sends video and keepalives, listens to
/// the client, and re-establishes the connection when it is lost.
///
/// It owns two threads: one that connects and then reads, and one that writes while a connection
/// is up. The capture pipeline's thread hands it encoded frames through <see cref="OfferAccessUnit"/>,
/// which never blocks.
/// </summary>
internal sealed class HostSession : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriterIdleWait = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan FastPingInterval = TimeSpan.FromMilliseconds(100);
    private const int FastPings = 5;

    private readonly HostSessionOptions _options;
    private readonly Func<StreamConfig> _streamConfig;
    private readonly Action<string> _log;
    private readonly Action _wake;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly VideoSendQueue _queue;
    private readonly ParameterSetTracker _parameterSets = new();
    private readonly KeyframeRequestLimiter _keyframeLimiter = new(KeyframeRequestLimiter.DefaultInterval);
    private readonly ClockOffsetEstimator _clock = new();
    private readonly ConcurrentQueue<byte[]> _control = new();
    private readonly ConcurrentDictionary<long, long> _pingsInFlight = new();

    private Thread? _thread;
    private TcpClient? _connection;
    private string _stopReason = "the host stopped the stream";

    private SessionPhase _phase = SessionPhase.Idle;
    private string? _endReason;
    private volatile bool _streaming;
    private int _keyframeWanted;
    private uint _sequence;

    private long _encoded;
    private long _encodedBytes;
    private long _notSentNoClient;
    private long _droppedAwaitingKeyframe;
    private long _droppedQueueFull;
    private long _discardedAtDisconnect;
    private long _droppedTooLarge;
    private long _transmitted;
    private long _transmittedBytes;
    private long _keyframesTransmitted;
    private long _keyframeRequests;
    private long _connectionsEstablished;
    private long _connectionsLost;
    private long _transmittedThisConnection;

    private ClientStatsSample? _latestSample;
    private ClientStatsSample? _previousSample;
    private ClientStats _finishedConnections = new();
    private readonly LatencyAggregate _receiveLatency = new();
    private readonly LatencyAggregate _renderLatency = new();
    private readonly LatencyAggregate _displayLatency = new();

    public HostSession(HostSessionOptions options, Func<StreamConfig> streamConfig, Action<string> log, Action wake)
    {
        _options = options;
        _streamConfig = streamConfig;
        _log = log;
        _wake = wake;
        _queue = new VideoSendQueue(options.SendQueueCapacity);
    }

    public SessionPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>Why the session ended, once <see cref="HasEnded"/> is true.</summary>
    public string? EndReason
    {
        get
        {
            lock (_gate)
            {
                return _endReason;
            }
        }
    }

    public bool HasEnded => Phase is SessionPhase.Disconnected or SessionPhase.Failed;

    /// <summary>True only while the handshake has completed and the connection is up.</summary>
    public bool IsStreaming => _streaming;

    public Hello? ClientHello { get; private set; }

    public ClientCapabilities? ClientCapabilities { get; private set; }

    public StreamReady? ClientReady { get; private set; }

    /// <summary>The most recent statistics the client sent on this connection, and when they arrived.</summary>
    public ClientStatsSample? LatestClientSample
    {
        get
        {
            lock (_gate)
            {
                return _latestSample;
            }
        }
    }

    /// <summary>The statistics message before the latest, for working out rates between the two.</summary>
    public ClientStatsSample? PreviousClientSample
    {
        get
        {
            lock (_gate)
            {
                return _previousSample;
            }
        }
    }

    /// <summary>
    /// The client's counters summed over every connection of this session. The client counts from
    /// zero on each connection, so the last report of each finished connection is added in.
    /// </summary>
    public ClientStats ClientTotals
    {
        get
        {
            lock (_gate)
            {
                return _latestSample is null ? _finishedConnections : _finishedConnections.Plus(_latestSample.Stats);
            }
        }
    }

    /// <summary>Latency reports from the client, combined over the whole session.</summary>
    public (LatencyAggregate Receive, LatencyAggregate Render, LatencyAggregate Display) Latency
    {
        get
        {
            lock (_gate)
            {
                return (_receiveLatency.Copy(), _renderLatency.Copy(), _displayLatency.Copy());
            }
        }
    }

    /// <summary>Video frames written to the socket since the current connection was established.</summary>
    public long TransmittedThisConnection => Interlocked.Read(ref _transmittedThisConnection);

    public ClockOffset? Clock => _clock.Best;

    public int QueueDepth => _queue.Count;

    public HostStreamCounters Counters => new()
    {
        Encoded = Interlocked.Read(ref _encoded),
        EncodedBytes = Interlocked.Read(ref _encodedBytes),
        NotSentNoClient = Interlocked.Read(ref _notSentNoClient),
        DroppedAwaitingKeyframe = Interlocked.Read(ref _droppedAwaitingKeyframe),
        DroppedQueueFull = Interlocked.Read(ref _droppedQueueFull),
        DiscardedAtDisconnect = Interlocked.Read(ref _discardedAtDisconnect),
        DroppedTooLarge = Interlocked.Read(ref _droppedTooLarge),
        Transmitted = Interlocked.Read(ref _transmitted),
        TransmittedBytes = Interlocked.Read(ref _transmittedBytes),
        KeyframesTransmitted = Interlocked.Read(ref _keyframesTransmitted),
        KeyframeRequests = Interlocked.Read(ref _keyframeRequests),
        ConnectionsEstablished = Interlocked.Read(ref _connectionsEstablished),
        ConnectionsLost = Interlocked.Read(ref _connectionsLost),
    };

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ExtendSpace host session" };
        _thread.Start();
    }

    /// <summary>
    /// Takes one encoded access unit from the capture pipeline. Never blocks: the frame is queued
    /// for the socket or it is dropped and counted.
    /// </summary>
    /// <param name="captureMicros">When the picture was captured, on the host's monotonic clock.</param>
    public void OfferAccessUnit(ReadOnlySpan<byte> accessUnit, long captureMicros)
    {
        Interlocked.Increment(ref _encoded);
        Interlocked.Add(ref _encodedBytes, accessUnit.Length);

        // Always looked at, so that the latest SPS and PPS are known whenever a client appears.
        VideoFlags flags = _parameterSets.Classify(accessUnit, out ReadOnlySpan<byte> parameterSets);

        if (!_streaming)
        {
            Interlocked.Increment(ref _notSentNoClient);
            return;
        }

        if (Protocol.VideoHeaderLength + parameterSets.Length + accessUnit.Length > Protocol.MaxVideoPayload)
        {
            Interlocked.Increment(ref _droppedTooLarge);
            RequestKeyframe();
            return;
        }

        uint sequence = _sequence++;
        EnqueueResult result = _queue.Enqueue(OutgoingVideoFrame.Create(sequence, captureMicros, flags, parameterSets, accessUnit));
        switch (result.Outcome)
        {
            case EnqueueOutcome.DroppedAwaitingKeyframe:
                Interlocked.Increment(ref _droppedAwaitingKeyframe);
                break;
            case EnqueueOutcome.DroppedQueueFull:
                Interlocked.Add(ref _droppedQueueFull, 1 + result.AlsoDropped);
                break;
            default:
                Interlocked.Add(ref _droppedQueueFull, result.AlsoDropped);
                break;
        }

        if (result.NeedsKeyframe)
        {
            RequestKeyframe();
        }
    }

    /// <summary>
    /// True, once, when the encoder should be asked for a keyframe. Requests are rate-limited, so a
    /// burst of them costs a single keyframe.
    /// </summary>
    public bool TakeKeyframeRequest()
    {
        if (Volatile.Read(ref _keyframeWanted) == 0 || !_keyframeLimiter.TryAcquire(HostClock.NowMicros))
        {
            return false;
        }

        Interlocked.Exchange(ref _keyframeWanted, 0);
        Interlocked.Increment(ref _keyframeRequests);
        return true;
    }

    /// <summary>Ends the session in an orderly way: says goodbye if connected, then closes. Returns when both threads are done.</summary>
    public void Stop(string reason)
    {
        lock (_gate)
        {
            _stopReason = reason;
        }

        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            Stop("the host stopped the stream");
        }

        _connection?.Dispose();
        _queue.Dispose();
        _stop.Dispose();
    }

    private void RequestKeyframe()
    {
        Interlocked.Exchange(ref _keyframeWanted, 1);
        _wake();
    }

    private void Run()
    {
        var backoff = new ReconnectBackoff(_options.ReconnectBudget);
        long waitingSince = HostClock.NowMicros;
        bool everStreamed = false;
        string? lastProblem = null;

        while (!_stop.IsCancellationRequested)
        {
            SetPhase(everStreamed ? SessionPhase.Reconnecting : SessionPhase.Connecting);
            SessionEnd end;
            try
            {
                end = RunConnection();
            }
            catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or ObjectDisposedException)
            {
                end = SessionEnd.Lost(Describe(exception));
            }
            catch (ProtocolException exception)
            {
                end = SessionEnd.Fatal($"{exception.Code}: {exception.Message}");
            }
            catch (Exception exception)
            {
                // Whatever it was, it ends this session with an explanation, not the process.
                end = SessionEnd.Fatal($"{ErrorCode.InternalError}: {exception.GetType().Name}: {exception.Message}");
            }

            if (end.WasStreaming)
            {
                everStreamed = true;
                backoff.Reset();
                waitingSince = HostClock.NowMicros;
            }

            if (_stop.IsCancellationRequested)
            {
                break;
            }

            switch (end.Kind)
            {
                case SessionEndKind.Goodbye:
                    Finish(SessionPhase.Disconnected, $"the client ended the session: {end.Detail}");
                    return;
                case SessionEndKind.Fatal:
                    Finish(SessionPhase.Failed, end.Detail);
                    return;
            }

            if (end.WasStreaming)
            {
                Interlocked.Increment(ref _connectionsLost);
                _log($"connection lost: {end.Detail}");
            }
            else if (end.Detail != lastProblem)
            {
                // The same "nothing is listening yet" every 250 ms is said once.
                _log($"not connected: {end.Detail}");
            }

            lastProblem = end.Detail;
            TimeSpan elapsed = TimeSpan.FromMicroseconds(HostClock.NowMicros - waitingSince);
            if (backoff.NextDelay(elapsed) is not { } delay)
            {
                Finish(
                    SessionPhase.Failed,
                    $"no connection could be {(everStreamed ? "re-established" : "established")} within {_options.ReconnectBudget.TotalSeconds:0} s; last problem: {end.Detail}");
                return;
            }

            _stop.Token.WaitHandle.WaitOne(delay);
        }

        Finish(SessionPhase.Disconnected, _stopReason);
    }

    /// <summary>One connection from connect to close. Returns why it ended.</summary>
    private SessionEnd RunConnection()
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            if (!client.ConnectAsync(IPAddress.Loopback, _options.Port).Wait(ConnectTimeout))
            {
                return SessionEnd.Lost($"connecting to 127.0.0.1:{_options.Port} timed out");
            }
        }
        catch (AggregateException exception) when (exception.InnerException is SocketException or IOException)
        {
            // Nothing is forwarding the port (yet): the ordinary "not there" case, to be retried.
            return SessionEnd.Lost($"connecting to 127.0.0.1:{_options.Port} failed: {Describe(exception)}");
        }

        _connection = client;
        NetworkStream stream = client.GetStream();
        var reader = new ConnectionReader(client.Client);

        SetPhase(SessionPhase.Negotiating);
        stream.WriteTimeout = (int)Protocol.HandshakeStepTimeout.TotalMilliseconds;
        stream.Write(Protocol.Preface);
        stream.Write(ControlCodec.Encode(new Hello(Protocol.MajorVersion, Protocol.MajorVersion, "host", _options.DeviceName, _options.AppVersion)));

        // adb accepts the TCP connection whether or not the app is listening behind it. Only the
        // client's own preface and hello prove there is an ExtendSpace client on the other end.
        Hello hello = reader.Expect<Hello>(Protocol.HandshakeStepTimeout);
        if (hello.ProtocolMin > Protocol.MajorVersion || hello.ProtocolMax < Protocol.MajorVersion)
        {
            string problem = $"the client speaks protocol versions {hello.ProtocolMin} to {hello.ProtocolMax}; this host speaks {Protocol.MajorVersion}";
            TryWrite(stream, new ErrorMessage(ErrorCode.ProtocolVersionUnsupported, problem, true));
            return SessionEnd.Fatal(problem);
        }

        ClientCapabilities capabilities = reader.Expect<ClientCapabilities>(Protocol.HandshakeStepTimeout);
        StreamConfig config = _streamConfig();
        if (!capabilities.Decoders.Any(decoder => decoder.MaxWidth >= config.Width && decoder.MaxHeight >= config.Height))
        {
            string problem = $"no decoder the client reports can take {config.Width} x {config.Height}";
            TryWrite(stream, new ErrorMessage(ErrorCode.NoCommonConfiguration, problem, true));
            return SessionEnd.Fatal(problem);
        }

        stream.Write(ControlCodec.Encode(config));
        StreamReady ready = reader.Expect<StreamReady>(Protocol.HandshakeStepTimeout);

        ClientHello = hello;
        ClientCapabilities = capabilities;
        ClientReady = ready;

        return RunStreaming(client, stream, reader, hello);
    }

    private SessionEnd RunStreaming(TcpClient client, NetworkStream stream, ConnectionReader reader, Hello hello)
    {
        // A new connection starts a new stream: sequence numbers from zero, and nothing is sent
        // before a keyframe that carries its parameter sets.
        _queue.Reset();
        _sequence = 0;
        while (_control.TryDequeue(out _))
        {
        }

        _pingsInFlight.Clear();
        Interlocked.Exchange(ref _transmittedThisConnection, 0);
        _streaming = true;
        RequestKeyframe();
        Interlocked.Increment(ref _connectionsEstablished);
        SetPhase(SessionPhase.Streaming);
        _log($"streaming to '{hello.DeviceName}' (app {hello.AppVersion}); the client will decode with {ClientReady?.Decoder ?? "an unnamed decoder"}");

        using var connectionEnded = new CancellationTokenSource();
        Exception? writerFailure = null;
        var writer = new Thread(() =>
        {
            try
            {
                WriteLoop(stream, connectionEnded.Token);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                writerFailure = exception;
                client.Close();
            }
        })
        {
            IsBackground = true,
            Name = "ExtendSpace host writer",
        };
        writer.Start();

        SessionEnd end;
        try
        {
            end = ReadLoop(reader);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            end = SessionEnd.Lost(writerFailure is null ? Describe(exception) : Describe(writerFailure));
        }
        catch (ProtocolException exception)
        {
            TryWrite(stream, new ErrorMessage(exception.Code, exception.Message, true));
            end = SessionEnd.Fatal($"{exception.Code}: {exception.Message}");
        }
        finally
        {
            _streaming = false;
            connectionEnded.Cancel();
            writer.Join(TimeSpan.FromSeconds(2));
            Interlocked.Add(ref _discardedAtDisconnect, _queue.Reset());
            lock (_gate)
            {
                if (_latestSample is not null)
                {
                    _finishedConnections = _finishedConnections.Plus(_latestSample.Stats);
                }

                _latestSample = null;
                _previousSample = null;
            }
        }

        if (_stop.IsCancellationRequested)
        {
            string reason;
            lock (_gate)
            {
                reason = _stopReason;
            }

            TryWrite(stream, new Bye(reason));
        }

        return end with { WasStreaming = true };
    }

    private SessionEnd ReadLoop(ConnectionReader reader)
    {
        while (!_stop.IsCancellationRequested)
        {
            if (!reader.TryNext(Protocol.KeepaliveTimeout, _stop.Token, out ReceivedFrame frame))
            {
                return _stop.IsCancellationRequested
                    ? SessionEnd.Lost("stopped")
                    : SessionEnd.Lost($"nothing was received from the client for {Protocol.KeepaliveTimeout.TotalSeconds:0} s");
            }

            if (frame.Header.Channel != Protocol.ChannelControl)
            {
                throw new ProtocolException(ErrorCode.UnexpectedMessage, $"The client sent a frame on channel {frame.Header.Channel}; only control messages are expected from it.");
            }

            switch (ControlCodec.Decode(frame.Header.Type, frame.Payload))
            {
                case Pong pong:
                    if (_pingsInFlight.TryRemove(pong.Id, out long sentMicros))
                    {
                        _clock.Add(sentMicros, frame.ReceivedMicros, pong.ClientTimeMicros);
                    }

                    break;
                case ClientStats stats:
                    lock (_gate)
                    {
                        _previousSample = _latestSample;
                        _latestSample = new ClientStatsSample(stats, frame.ReceivedMicros);
                        _receiveLatency.Add(stats.ReceiveLatency);
                        _renderLatency.Add(stats.RenderLatency);
                        _displayLatency.Add(stats.DisplayLatency);
                    }

                    break;
                case KeyframeRequest request:
                    _log($"the client asked for a keyframe ({request.Reason})");
                    RequestKeyframe();
                    break;
                case ErrorMessage error:
                    _log($"the client reported {(error.Fatal ? "a fatal" : "an")} error {error.Code}: {error.Message}");
                    if (error.Fatal)
                    {
                        return SessionEnd.Fatal($"the client reported {error.Code}: {error.Message}");
                    }

                    break;
                case Bye bye:
                    return SessionEnd.Goodbye(bye.Reason);
                case null:
                    // A control type from a later protocol version: ignored, as the protocol requires.
                    break;
                default:
                    throw new ProtocolException(ErrorCode.UnexpectedMessage, $"The client sent a {(ControlType)frame.Header.Type} message, which a client does not send while streaming.");
            }
        }

        return SessionEnd.Lost("stopped");
    }

    private void WriteLoop(NetworkStream stream, CancellationToken connectionEnded)
    {
        stream.WriteTimeout = (int)Protocol.KeepaliveTimeout.TotalMilliseconds;
        long pingId = 0;
        long nextPingMicros = HostClock.NowMicros;

        while (!connectionEnded.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            while (_control.TryDequeue(out byte[]? control))
            {
                stream.Write(control);
            }

            long now = HostClock.NowMicros;
            if (now >= nextPingMicros)
            {
                ClockOffset? clock = _clock.Best;
                long id = ++pingId;

                // The send time is taken as late as possible, right before the write.
                long sendMicros = HostClock.NowMicros;
                _pingsInFlight[id] = sendMicros;
                stream.Write(ControlCodec.Encode(new Ping(id, sendMicros, clock?.OffsetMicros, clock?.RoundTripMicros)));

                // A few quick pings first, so that a clock estimate exists within the first second.
                TimeSpan interval = id <= FastPings ? FastPingInterval : Protocol.KeepaliveInterval;
                nextPingMicros = sendMicros + (long)interval.TotalMicroseconds;
                ForgetOldPings(id);
            }

            if (_queue.TryDequeue(WriterIdleWait, out OutgoingVideoFrame? frame) && frame is not null)
            {
                try
                {
                    stream.Write(frame.Bytes);
                    Interlocked.Increment(ref _transmitted);
                    Interlocked.Increment(ref _transmittedThisConnection);
                    Interlocked.Add(ref _transmittedBytes, frame.Length);
                    if (frame.Flags.HasFlag(VideoFlags.Keyframe))
                    {
                        Interlocked.Increment(ref _keyframesTransmitted);
                    }
                }
                catch
                {
                    // Taken from the queue but never fully written: it was lost with the connection.
                    Interlocked.Increment(ref _discardedAtDisconnect);
                    throw;
                }
                finally
                {
                    frame.Release();
                }
            }
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

    /// <summary>Best effort: used for a last message on a connection that is being given up anyway.</summary>
    private void TryWrite(NetworkStream stream, object message)
    {
        try
        {
            stream.WriteTimeout = 500;
            stream.Write(ControlCodec.Encode(message));
            stream.Flush();
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            _log($"could not send the final {message.GetType().Name} message: {exception.Message}");
        }
    }

    private void SetPhase(SessionPhase phase)
    {
        lock (_gate)
        {
            if (_phase == phase)
            {
                return;
            }

            _phase = phase;
        }

        _log($"state: {phase}");
        _wake();
    }

    private void Finish(SessionPhase phase, string reason)
    {
        _streaming = false;
        lock (_gate)
        {
            _phase = phase;
            _endReason = reason;
        }

        _log($"state: {phase} ({reason})");
        _wake();
    }

    private static string Describe(Exception exception)
    {
        Exception root = exception is AggregateException { InnerException: { } inner } ? inner : exception;
        return root is SocketException socket
            ? $"{socket.SocketErrorCode}: {socket.Message}"
            : root.InnerException is SocketException innerSocket
                ? $"{innerSocket.SocketErrorCode}: {innerSocket.Message}"
                : root.Message;
    }

    private enum SessionEndKind
    {
        Lost,
        Goodbye,
        Fatal,
    }

    private readonly record struct SessionEnd(SessionEndKind Kind, string Detail, bool WasStreaming = false)
    {
        public static SessionEnd Lost(string detail) => new(SessionEndKind.Lost, detail);

        public static SessionEnd Goodbye(string detail) => new(SessionEndKind.Goodbye, detail);

        public static SessionEnd Fatal(string detail) => new(SessionEndKind.Fatal, detail);
    }
}
