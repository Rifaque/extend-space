namespace ExtendSpace.Media;

/// <summary>
/// Lets the capture callback and the encoder's event pump wake the pipeline thread. Signals
/// coalesce, and one that arrives after disposal (a callback already in flight) is ignored.
/// </summary>
internal sealed class WakeSignal : IDisposable
{
    private readonly Lock _gate = new();
    private AutoResetEvent? _event = new(false);

    public void Set()
    {
        lock (_gate)
        {
            _event?.Set();
        }
    }

    /// <summary>Called only by the owning thread, before it disposes the signal.</summary>
    public bool Wait(TimeSpan timeout) =>
        (_event ?? throw new ObjectDisposedException(nameof(WakeSignal))).WaitOne(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            _event?.Dispose();
            _event = null;
        }
    }
}
