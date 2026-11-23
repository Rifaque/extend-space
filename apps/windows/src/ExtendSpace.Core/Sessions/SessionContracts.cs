namespace ExtendSpace.Core.Sessions;

// The three seams between the session coordinator and everything it directs. Nothing in the
// application implements the first two with real hardware yet; SessionCoordinator is written and
// tested against substitutes before any socket, adb command, capture session or encoder is
// involved. See docs/architecture/production-session-architecture.md, sections 5 and 15.
//
// Rules every implementation of ISessionConnection and IVideoPipeline follows:
//
// - One call is one attempt. Nothing behind a seam retries, waits for a device to appear, or
//   decides that a session is over; it reports an outcome and the coordinator decides.
// - A call that takes a CancellationToken reports OutcomeKind.Cancelled (or, for a void task, ends
//   as cancelled) when the token was observed. It never turns cancellation into a fault.
// - Cancellation must actually end a call that is blocked: a call waiting on a socket read, a
//   connect or an adb command closes or kills what it waits on when its token is cancelled. The
//   coordinator waits (bounded) for a cancelled call to return before it calls CloseAsync or
//   StopAsync, so a call that only notices cancellation once its I/O finishes by itself would hold
//   teardown until the deadline.
// - A step that fails or is cancelled leaves nothing of its own acquired: what it opened, it closes
//   before returning. Resources that earlier successful steps acquired stay until StopAsync or
//   CloseAsync.
// - The coordinator never runs two calls on the same seam at once, except the ones documented as
//   callable from any thread (OfferVideo, RequestKeyframe, SetEncoding) and StopAsync or CloseAsync
//   after a step has overrun the teardown deadline.
// - Returned tasks are completed with TaskCreationOptions.RunContinuationsAsynchronously by real
//   implementations, so that the coordinator's continuation never runs on a component's own reader,
//   writer or pump thread. Test fakes complete them inline, which is what makes the coordinator's
//   tests run on the test's own thread.

/// <summary>What the encoded stream is, as the host will describe it to the client.</summary>
public sealed record StreamSettings(int Width, int Height, int FrameRate, int BitrateBitsPerSecond, string EncoderName, bool EncoderIsHardware);

/// <summary>Who answered, once the stream has been configured and the client said it is ready.</summary>
public sealed record PeerDescription(string DeviceName, string AppVersion, string? DecoderName, bool? DecoderIsHardware);

/// <summary>One H.264 decoder the client says it has. Mirrors the <c>capabilities</c> message.</summary>
public sealed record DecoderCapability(string Name, bool IsHardware, bool LowLatency, int MaxWidth, int MaxHeight)
{
    public bool Fits(int width, int height) => MaxWidth >= width && MaxHeight >= height;
}

/// <summary>What the client said about itself before any stream was chosen: its hello and its capabilities.</summary>
public sealed record ClientCapabilities(
    string DeviceName,
    string AppVersion,
    int DisplayWidth,
    int DisplayHeight,
    double RefreshHz,
    IReadOnlyList<DecoderCapability> Decoders)
{
    /// <summary>Whether any decoder the client listed can take a picture of this size.</summary>
    public bool CanDecode(int width, int height) => Decoders.Any(decoder => decoder.Fits(width, height));
}

public enum ConnectionEndKind
{
    /// <summary>Lost without a goodbye: closed, reset, or silent for the keepalive time. Worth reconnecting.</summary>
    Lost,

    /// <summary>The peer said goodbye. The session is over and is not to be re-established.</summary>
    PeerEnded,

    /// <summary>Ended by something trying again cannot cure: a protocol violation, a fatal error from the peer.</summary>
    Fatal,

    /// <summary>Closed by this side, through <see cref="ISessionConnection.CloseAsync"/>.</summary>
    Closed,
}

/// <summary>Why a connection whose stream had been configured is over.</summary>
/// <param name="Fault">Set for <see cref="ConnectionEndKind.Lost"/> (retryable) and <see cref="ConnectionEndKind.Fatal"/> (terminal).</param>
/// <param name="PeerReason">The peer's own words, for <see cref="ConnectionEndKind.PeerEnded"/>. For logs only.</param>
public sealed record ConnectionEnd(ConnectionEndKind Kind, SessionFault? Fault = null, string? PeerReason = null);

/// <summary>Receives encoded video. Called on the pipeline's thread; must return without waiting.</summary>
/// <param name="captureMicros">When the picture was captured, on the host's monotonic clock.</param>
public delegate void EncodedVideoSink(ReadOnlySpan<byte> accessUnit, long captureMicros);

/// <summary>
/// One connection to the client at a time, from the coordinator's side. Behind it are the transport
/// (for USB: adb and its port forward), the socket, the handshake and the keepalive; none of them
/// shows through.
///
/// A connection attempt is three calls, in order: <see cref="OpenAsync"/>,
/// <see cref="ExchangeCapabilitiesAsync"/>, <see cref="ConfigureStreamAsync"/>. The coordinator
/// chooses the stream between the second and the third, from what the second returned. Only after
/// the third has succeeded is there a stream, and only then may <see cref="WaitForEndAsync"/> be
/// called.
/// </summary>
public interface ISessionConnection
{
    /// <summary>
    /// Sets the transport up, or sets it up again, and connects. For the first attempt of a session
    /// and for every later one alike. A connection left over from an earlier attempt or one that has
    /// ended is discarded first; no <see cref="CloseAsync"/> is needed between attempts.
    /// </summary>
    Task<AttemptOutcome> OpenAsync(CancellationToken cancellation);

    /// <summary>
    /// On the open connection: the preface and <c>hello</c> both ways, and the client's
    /// <c>capabilities</c>. Fails with a terminal <see cref="FaultCategory.Protocol"/> fault for a
    /// version mismatch (having told the client), and retryably when nothing answers in time.
    /// Reports <see cref="OutcomeKind.PeerEnded"/> if the client says <c>bye</c> instead, as
    /// <see cref="ConfigureStreamAsync"/> does.
    /// </summary>
    Task<AttemptOutcome<ClientCapabilities>> ExchangeCapabilitiesAsync(CancellationToken cancellation);

    /// <summary>
    /// Sends <c>stream_config</c> for <paramref name="stream"/> and waits for <c>stream_ready</c>.
    /// From a successful return the connection takes video through <see cref="OfferVideo"/>,
    /// starting with sequence number zero and refusing anything before a keyframe. A client that
    /// rejects the stream is a terminal <see cref="FaultCategory.Protocol"/> fault with the code
    /// <c>stream_config_rejected</c>.
    /// </summary>
    Task<AttemptOutcome<PeerDescription>> ConfigureStreamAsync(StreamSettings stream, CancellationToken cancellation);

    /// <summary>
    /// Completes once, when the connection whose stream was configured is over, saying why. Valid
    /// only after <see cref="ConfigureStreamAsync"/> has succeeded on the current connection; before
    /// that there is nothing to wait for and the call throws <see cref="InvalidOperationException"/>.
    /// Cancelling the wait ends the task as cancelled and does not close the connection.
    /// </summary>
    Task<ConnectionEnd> WaitForEndAsync(CancellationToken cancellation);

    /// <summary>
    /// Takes one encoded access unit for sending. Never blocks: a frame that cannot be queued is
    /// dropped and counted, and <see cref="KeyframeWanted"/> is raised if that leaves the client
    /// unable to decode what follows. Ignored when no stream is configured.
    /// </summary>
    void OfferVideo(ReadOnlySpan<byte> accessUnit, long captureMicros);

    /// <summary>
    /// Raised, on any thread, when the client needs a keyframe: it sent <c>request_keyframe</c>
    /// (passed on at most once per 500 ms), or frames were dropped before reaching it. It is
    /// <b>not</b> raised when a connection begins: asking for the keyframe that starts every stream
    /// is the coordinator's job alone.
    /// </summary>
    event Action? KeyframeWanted;

    /// <summary>
    /// Ends the session's use of the connection: the current connection if any, then the transport
    /// (the adb port forward). If <paramref name="fault"/> is given and the client can still be
    /// told, the last message is a fatal <c>error</c> derived from it; otherwise a <c>bye</c>
    /// carrying <paramref name="reason"/>. Either is sent only if it can be sent promptly. Safe to
    /// call in any state and more than once. Bounded by the implementation; cancellation shortens it
    /// further and never leaves anything open.
    /// </summary>
    Task CloseAsync(string reason, SessionFault? fault, CancellationToken cancellation);
}

/// <summary>Which display the session is to show.</summary>
/// <param name="MonitorIndex">Null for the primary monitor.</param>
/// <param name="MonitorDeviceName">When set, the monitor with this device name, or a terminal <c>monitor_not_found</c> if it is gone: never another one (D68).</param>
public sealed record VideoSourceRequest(int? MonitorIndex, string? MonitorDeviceName = null);

/// <summary>A monitor the user can choose to mirror (D68).</summary>
/// <param name="DeviceName">Windows' name for it, for example <c>\\.\DISPLAY1</c>; what a session asks for.</param>
/// <param name="RefreshHz">As Windows reports it; null if it will not say.</param>
public sealed record DisplayInfo(string DeviceName, bool IsPrimary, int Width, int Height, int? RefreshHz)
{
    /// <summary>"Display 2" for <c>\\.\DISPLAY2</c>, which is how Windows Settings numbers displays; the device name otherwise.</summary>
    public static string LabelFor(string deviceName)
    {
        const string Prefix = @"\\.\DISPLAY";
        return deviceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(deviceName.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            ? $"Display {number}"
            : deviceName;
    }
}

/// <summary>Lists the monitors attached now. Implemented by the media assembly; quick enough for the UI thread.</summary>
public interface IDisplayCatalog
{
    IReadOnlyList<DisplayInfo> List();
}

/// <summary>The display source once acquired, before anything is encoded.</summary>
public sealed record VideoSourceDescription(int Width, int Height, double RefreshHz);

/// <summary>How the coordinator wants the acquired source encoded.</summary>
public sealed record EncodingRequest(int Width, int Height, int FrameRate, int BitrateBitsPerSecond);

/// <summary>
/// The capture-and-encode pipeline, from the coordinator's side: a display goes in, encoded video
/// comes out. It knows nothing of sessions, sockets or clients.
///
/// Its life is <see cref="AcquireAsync"/>, <see cref="PrepareAsync"/>, <see cref="Start"/>, any
/// number of <see cref="SetEncoding"/> and <see cref="RequestKeyframe"/>, then
/// <see cref="StopAsync"/>. After <see cref="StopAsync"/> it can be acquired again.
/// </summary>
public interface IVideoPipeline
{
    /// <summary>
    /// Resolves the monitor and creates what capturing it needs. Nothing is captured and no encoder
    /// is opened. Fails with a <see cref="FaultCategory.Capture"/> fault.
    /// </summary>
    Task<AttemptOutcome<VideoSourceDescription>> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation);

    /// <summary>
    /// Opens an encoder for exactly <paramref name="request"/>, scaling the acquired source to fit.
    /// Still nothing is captured. Fails with an <see cref="FaultCategory.Encoder"/> fault.
    /// </summary>
    Task<AttemptOutcome<StreamSettings>> PrepareAsync(EncodingRequest request, CancellationToken cancellation);

    /// <summary>Starts capturing. Encoded video goes to <paramref name="sink"/> while encoding is on. Returns at once.</summary>
    void Start(EncodedVideoSink sink);

    /// <summary>
    /// Off releases captured frames unencoded, which is what a session does while it has no client
    /// to send to; nothing stale is kept for later. On resumes at the next captured frame. May be
    /// called from any thread; returns at once.
    /// </summary>
    void SetEncoding(bool enabled);

    /// <summary>
    /// Makes a keyframe owed. May be called from any thread, at any time, any number of times;
    /// returns at once. The obligation is a standing one, not a pulse:
    /// <list type="bullet">
    /// <item>It lasts until a keyframe has left the encoder for the sink. A request made before
    /// <see cref="Start"/>, or while encoding is off, is still owed when encoding is next on.</item>
    /// <item>It is met even if the screen is still: the keyframe is made from the last picture
    /// captured, or from a fresh one, without waiting for the screen to change.</item>
    /// <item>Any number of requests while one is owed produce one keyframe.</item>
    /// <item>Requests while the pipeline is not acquired, or after <see cref="StopAsync"/>, are ignored.</item>
    /// </list>
    /// </summary>
    void RequestKeyframe();

    /// <summary>
    /// Completes with the fault if the pipeline fails after a successful <see cref="AcquireAsync"/>
    /// (the monitor changed, the encoder failed), and with null when it is stopped. Cancelling the
    /// wait does not stop it.
    /// </summary>
    Task<SessionFault?> WaitForFaultAsync(CancellationToken cancellation);

    /// <summary>
    /// Stops capturing, lets the encoder finish, and releases everything. Safe to call in any state
    /// and more than once. Bounded by the implementation.
    /// </summary>
    Task StopAsync(CancellationToken cancellation);
}

/// <summary>
/// Time, as the coordinator sees it. Every wait the coordinator makes (backoff between attempts,
/// the reconnect budget, teardown bounds) goes through this, so that its tests can move time by
/// hand instead of sleeping.
/// </summary>
public interface ISessionClock
{
    /// <summary>Monotonic time since an arbitrary start. Only differences mean anything.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Completes after <paramref name="delay"/> of this clock's time, or is cancelled.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellation);
}

/// <summary>The real clock.</summary>
public sealed class SystemSessionClock : ISessionClock
{
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    public TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_start);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
}
