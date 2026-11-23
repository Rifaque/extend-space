using System.Diagnostics.CodeAnalysis;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Media;

// The seam between DesktopVideoPipeline's policy (when to capture, encode, force a keyframe, stop)
// and the Windows objects that do the work. The real implementations are DesktopCaptureSource and
// MediaFoundationFrameEncoder, thin wrappers over the experiment's MonitorCapture, Nv12Converter,
// EncoderInputPool and H264Encoder. Tests substitute scripted ones, so that the policy is tested
// without a display, a GPU or an encoder.
//
// Threads: OpenSource and OpenEncoder run on a thread-pool thread, one at a time. Everything after
// that runs on the pipeline's pump thread, except Dispose, which runs on whichever thread releases
// the pipeline once the pump thread has ended (or never started). No member is called concurrently.

/// <summary>Opens a display source. One per pipeline.</summary>
internal interface IMediaBackend
{
    /// <summary>
    /// Resolves the monitor and creates what capturing it needs; nothing is captured yet. Blocking
    /// and short (device and capture-item creation). Throws <see cref="MediaSourceException"/> when
    /// the display cannot be captured.
    /// </summary>
    /// <param name="wake">Called, on any thread, whenever the source or a later encoder has something new.</param>
    ICaptureSource OpenSource(VideoSourceRequest request, Action wake);
}

/// <summary>An acquired display: a capture session that has not started, and the device it shares with an encoder.</summary>
internal interface ICaptureSource : IDisposable
{
    VideoSourceDescription Description { get; }

    /// <summary>Lines for diagnostics: the monitor, the adapter, how the border and rate are configured.</summary>
    IReadOnlyList<string> Details { get; }

    /// <summary>
    /// Opens an encoder for exactly <paramref name="request"/> on this source's device, scaling the
    /// picture to fit, and sets the capture rate to match. Throws <see cref="MediaEncoderException"/>
    /// when no encoder can be opened for it.
    /// </summary>
    /// <param name="onOutput">Receives each encoded access unit, on the thread that drives the encoder.</param>
    IFrameEncoder OpenEncoder(EncodingRequest request, EncodedOutputHandler onOutput, Action wake);

    void Start();

    /// <summary>Stops taking frames; a frame waiting to be taken is discarded.</summary>
    void Stop();

    /// <summary>Takes the newest captured frame, if one has arrived since the last call. The caller disposes it.</summary>
    bool TryTake([NotNullWhen(true)] out ICapturedFrame? frame);

    /// <summary>Set once the source has become unusable (monitor gone, size changed); never cleared.</summary>
    string? Failure { get; }

    CaptureActivity Activity { get; }
}

/// <summary>One captured picture, held until disposed. Holding it keeps one of the capture pool's buffers.</summary>
internal interface ICapturedFrame : IDisposable
{
    /// <summary>The <see cref="System.Diagnostics.Stopwatch"/> timestamp at which Windows handed the frame to this process.</summary>
    long Arrival { get; }
}

/// <param name="data">One encoded access unit, Annex B, valid only during the call.</param>
/// <param name="sampleTime">The sample time of the frame it was encoded from, as given to <see cref="IFrameEncoder.Submit"/>.</param>
internal delegate void EncodedOutputHandler(ReadOnlySpan<byte> data, long sampleTime);

/// <summary>An open encoder, with the conversion in front of it. Driven from the pump thread only.</summary>
internal interface IFrameEncoder : IDisposable
{
    StreamSettings Settings { get; }

    IReadOnlyList<string> Details { get; }

    /// <summary>Whether a frame may be submitted now. An asynchronous encoder has to ask for input first.</summary>
    bool CanAcceptInput { get; }

    /// <summary>Handles what the encoder announced since the last call; may deliver output. Throws <see cref="MediaEncoderException"/> on an encoder failure.</summary>
    void ProcessEvents();

    /// <summary>Asks for the next encoded picture to be an IDR picture. False if the encoder refused.</summary>
    bool ForceKeyframe();

    /// <summary>
    /// Converts <paramref name="frame"/> and gives it to the encoder. False, having submitted nothing,
    /// when every encoder input surface is still in use. The frame stays the caller's. Throws
    /// <see cref="MediaEncoderException"/> on an encoder failure.
    /// </summary>
    /// <param name="sampleTime">In 100 ns units; strictly increasing.</param>
    bool Submit(ICapturedFrame frame, long sampleTime);

    /// <summary>
    /// What the encoder's output has been read to be (size, profile, level), once a parameter set has
    /// been checked against <see cref="Settings"/>; null before that, or if this encoder does not check.
    /// </summary>
    string? ProducedStream => null;
}

/// <summary>What the capture source has seen, cumulative since it was acquired. Used to tell a still desktop from starved callbacks.</summary>
/// <param name="Callbacks">Times Windows raised the frame-arrived event while capturing.</param>
/// <param name="EmptyCallbacks">Of those, the times there was no frame to take.</param>
/// <param name="Delivered">Frames Windows handed over.</param>
/// <param name="SkippedByPacer">Frames released because they came sooner than the frame rate allows.</param>
/// <param name="ReplacedBeforeTaken">Frames overtaken by a newer one before the pump took them.</param>
/// <param name="LastArrival">The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the newest frame; null before the first.</param>
internal sealed record CaptureActivity(long Callbacks, long EmptyCallbacks, long Delivered, long SkippedByPacer, long ReplacedBeforeTaken, long? LastArrival)
{
    public static readonly CaptureActivity None = new(0, 0, 0, 0, 0, null);
}

/// <summary>The display could not be acquired or stopped being capturable.</summary>
internal sealed class MediaSourceException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>No encoder could be opened, or the encoder failed.</summary>
internal sealed class MediaEncoderException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
