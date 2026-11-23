namespace ExtendSpace.Core.Sessions;

/// <summary>
/// Ending the session when the application closes (D70): the window waits, bounded, for the
/// session's own teardown, so that the device is told goodbye and the adb forward is removed instead
/// of both being left to the process's death.
/// </summary>
public static class SessionShutdown
{
    /// <summary>Just over the host's 10 s teardown deadline (D50), so a teardown that keeps it is always waited for.</summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(11);

    /// <summary>Whether closing now would cut a session short.</summary>
    public static bool NeedsStop(SessionPhase phase) =>
        phase is SessionPhase.Connecting or SessionPhase.Negotiating or SessionPhase.Streaming or SessionPhase.Reconnecting or SessionPhase.Disconnecting;

    /// <summary>
    /// Stops the session and completes once it is <see cref="SessionPhase.Disconnected"/> or
    /// <see cref="SessionPhase.Failed"/>, or when <paramref name="bound"/> has passed. True if it ended
    /// within the bound. Never throws for the session's own state.
    /// </summary>
    public static async Task<bool> StopAndWaitAsync(ISessionControl session, TimeSpan bound, CancellationToken cancellation = default)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (!NeedsStop(session.State.Phase))
            {
                ended.TrySetResult();
            }
        }

        session.StateChanged += OnChanged;
        try
        {
            session.Stop();
            OnChanged(null, EventArgs.Empty);
            Task finished = await Task.WhenAny(ended.Task, Task.Delay(bound, cancellation)).ConfigureAwait(false);
            return finished == ended.Task;
        }
        finally
        {
            session.StateChanged -= OnChanged;
        }
    }
}
