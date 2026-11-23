namespace ExtendSpace.Core.Sessions;

// Stand-ins for components that do not exist yet. The host composes its coordinator with the
// pipeline stand-in until capture and encoding are moved out of the experiment (stage 4); the
// connection stand-in is no longer composed since the real one exists (stage 3, HostComposition),
// and is kept for tests of a coordinator with nothing real behind it. Connect is disabled by
// SessionCapabilities.CurrentBuild, so in practice nothing reaches them; if something did, the
// session would fail at its first step with a fault that says why, instead of hanging.

/// <summary>A pipeline that says it is not built yet, at the first step.</summary>
public sealed class UnavailableVideoPipeline : IVideoPipeline
{
    private static readonly SessionFault NotBuilt = new(FaultCategory.Capture, "capture_not_built", "Display capture is not part of this build yet.", FaultTreatment.Terminal);

    public Task<AttemptOutcome<VideoSourceDescription>> AcquireAsync(VideoSourceRequest request, CancellationToken cancellation) =>
        Task.FromResult(AttemptOutcome<VideoSourceDescription>.Failed(NotBuilt));

    public Task<AttemptOutcome<StreamSettings>> PrepareAsync(EncodingRequest request, CancellationToken cancellation) =>
        Task.FromResult(AttemptOutcome<StreamSettings>.Failed(NotBuilt with { Category = FaultCategory.Encoder, Code = "encoder_not_built", Message = "Video encoding is not part of this build yet." }));

    public void Start(EncodedVideoSink sink)
    {
    }

    public void SetEncoding(bool enabled)
    {
    }

    public void RequestKeyframe()
    {
    }

    public Task<SessionFault?> WaitForFaultAsync(CancellationToken cancellation) => Task.Delay(Timeout.Infinite, cancellation).ContinueWith(
        static _ => (SessionFault?)null,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
}

/// <summary>A connection that says it is not built yet, at the first step.</summary>
public sealed class UnavailableSessionConnection : ISessionConnection
{
    private static readonly SessionFault NotBuilt = new(FaultCategory.Transport, "transport_not_built", "The USB transport is not part of this build yet.", FaultTreatment.Terminal);

    public event Action? KeyframeWanted
    {
        add { }
        remove { }
    }

    public Task<AttemptOutcome> OpenAsync(CancellationToken cancellation) => Task.FromResult(AttemptOutcome.Failed(NotBuilt));

    public Task<AttemptOutcome<ClientCapabilities>> ExchangeCapabilitiesAsync(CancellationToken cancellation) =>
        Task.FromResult(AttemptOutcome<ClientCapabilities>.Failed(NotBuilt));

    public Task<AttemptOutcome<PeerDescription>> ConfigureStreamAsync(StreamSettings stream, CancellationToken cancellation) =>
        Task.FromResult(AttemptOutcome<PeerDescription>.Failed(NotBuilt));

    public Task<ConnectionEnd> WaitForEndAsync(CancellationToken cancellation) =>
        throw new InvalidOperationException("No stream has been configured.");

    public void OfferVideo(ReadOnlySpan<byte> accessUnit, long captureMicros)
    {
    }

    public Task CloseAsync(string reason, SessionFault? fault, CancellationToken cancellation) => Task.CompletedTask;
}
