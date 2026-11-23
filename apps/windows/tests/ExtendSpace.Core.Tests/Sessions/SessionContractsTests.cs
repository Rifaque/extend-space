using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

public class SessionFaultTests
{
    [Fact]
    public void A_fault_says_whether_trying_again_can_help()
    {
        var absent = new SessionFault(FaultCategory.Transport, "device_not_found", "No device is attached.", FaultTreatment.Retryable);
        var mismatch = new SessionFault(FaultCategory.Protocol, "protocol_version_unsupported", "Another version.", FaultTreatment.Terminal);

        Assert.True(absent.IsRetryable);
        Assert.False(mismatch.IsRetryable);
        Assert.False(absent.UserActionable);
    }

    [Fact]
    public void An_unexpected_exception_keeps_its_type_and_message_and_is_terminal()
    {
        SessionFault fault = SessionFault.Unexpected(new InvalidOperationException("the pool was empty"));

        Assert.Equal(FaultCategory.Internal, fault.Category);
        Assert.Equal(FaultTreatment.Terminal, fault.Treatment);
        Assert.Equal("InvalidOperationException: the pool was empty", fault.Message);
    }

    [Fact]
    public void An_outcome_is_exactly_one_of_succeeded_failed_and_cancelled()
    {
        var fault = new SessionFault(FaultCategory.Connection, "refused", "Refused.", FaultTreatment.Retryable);
        var peer = new PeerDescription("Pad", "1.0", "decoder", true);

        Assert.Equal((OutcomeKind.Succeeded, null), (AttemptOutcome.Succeeded.Kind, AttemptOutcome.Succeeded.Fault));
        Assert.Equal((OutcomeKind.Failed, fault), (AttemptOutcome.Failed(fault).Kind, AttemptOutcome.Failed(fault).Fault));

        // Cancellation carries no fault: it is not something that went wrong.
        Assert.Equal((OutcomeKind.Cancelled, null), (AttemptOutcome.Cancelled.Kind, AttemptOutcome.Cancelled.Fault));

        AttemptOutcome<PeerDescription> succeeded = AttemptOutcome<PeerDescription>.Succeeded(peer);
        Assert.Equal((OutcomeKind.Succeeded, peer, null), (succeeded.Kind, succeeded.Value, succeeded.Fault));

        AttemptOutcome<PeerDescription> failed = AttemptOutcome<PeerDescription>.Failed(fault);
        Assert.Equal((OutcomeKind.Failed, null, fault), (failed.Kind, failed.Value, failed.Fault));

        AttemptOutcome<PeerDescription> cancelled = AttemptOutcome<PeerDescription>.Cancelled;
        Assert.Equal((OutcomeKind.Cancelled, null, null), (cancelled.Kind, cancelled.Value, cancelled.Fault));
    }

    [Fact]
    public void A_failure_without_a_fault_and_a_success_without_a_value_cannot_be_made()
    {
        Assert.Throws<ArgumentNullException>(() => AttemptOutcome.Failed(null!));
        Assert.Throws<ArgumentNullException>(() => AttemptOutcome<PeerDescription>.Failed(null!));
        Assert.Throws<ArgumentNullException>(() => AttemptOutcome<PeerDescription>.Succeeded(null!));
    }
}

/// <summary>
/// The clock seam, proven usable: a clock a test moves by hand makes a wait finish exactly when
/// the test says, without the test sleeping.
/// </summary>
public class SessionClockTests
{
    [Fact]
    public void A_delay_completes_when_the_clock_is_moved_past_it_and_not_before()
    {
        var clock = new ManualSessionClock();

        Task delay = clock.DelayAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3.999));
        Assert.False(delay.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(delay.IsCompletedSuccessfully);
        Assert.Equal(TimeSpan.FromSeconds(4), clock.Elapsed);
    }

    [Fact]
    public void Delays_complete_in_the_order_they_fall_due()
    {
        var clock = new ManualSessionClock();
        var order = new List<string>();
        Task late = clock.DelayAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => order.Add("late"), TaskContinuationOptions.ExecuteSynchronously);
        Task early = clock.DelayAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ContinueWith(_ => order.Add("early"), TaskContinuationOptions.ExecuteSynchronously);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(late.IsCompleted && early.IsCompleted);
        Assert.Equal(["early", "late"], order);
    }

    [Fact]
    public void A_cancelled_delay_ends_as_cancelled_and_is_forgotten()
    {
        var clock = new ManualSessionClock();
        using var cancellation = new CancellationTokenSource();

        Task delay = clock.DelayAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();

        Assert.True(delay.IsCanceled);
        Assert.Equal(0, clock.PendingDelays);
    }

    [Fact]
    public void A_delay_of_no_time_and_a_delay_already_cancelled_complete_at_once()
    {
        var clock = new ManualSessionClock();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.True(clock.DelayAsync(TimeSpan.Zero, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(clock.DelayAsync(TimeSpan.FromSeconds(1), cancelled.Token).IsCanceled);
    }

    [Fact]
    public async Task The_real_clock_moves_forward_and_its_delays_can_be_cancelled()
    {
        var clock = new SystemSessionClock();
        TimeSpan before = clock.Elapsed;
        await clock.DelayAsync(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed > before);

        using var cancellation = new CancellationTokenSource();
        Task delay = clock.DelayAsync(TimeSpan.FromMinutes(10), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
    }
}

/// <summary>A clock that stands still until a test moves it. Not thread-safe; tests drive it from one thread.</summary>
internal sealed class ManualSessionClock : ISessionClock
{
    private readonly List<(TimeSpan Due, TaskCompletionSource Source)> _delays = [];

    public TimeSpan Elapsed { get; private set; }

    public int PendingDelays => _delays.Count;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellation);
        }

        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var source = new TaskCompletionSource();
        var entry = (Elapsed + delay, source);
        _delays.Add(entry);
        cancellation.Register(() =>
        {
            if (_delays.Remove(entry))
            {
                source.TrySetCanceled(cancellation);
            }
        });
        return source.Task;
    }

    public void Advance(TimeSpan by)
    {
        TimeSpan target = Elapsed + by;
        while (_delays.Where(delay => delay.Due <= target).OrderBy(delay => delay.Due).Cast<(TimeSpan Due, TaskCompletionSource Source)?>().FirstOrDefault() is { } next)
        {
            _delays.Remove(next);
            Elapsed = next.Due;
            next.Source.TrySetResult();
        }

        Elapsed = target;
    }
}
