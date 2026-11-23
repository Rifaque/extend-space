using System.Net;
using System.Net.Sockets;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Connection;

/// <summary>
/// Decision D33 against real sockets: cancelling a call that is blocked must end it promptly,
/// because it closes what the call is blocked on; a cancelled call reports
/// <see cref="OutcomeKind.Cancelled"/>, never a fault; and nothing it held is left open. "Promptly"
/// is asserted as well under a second for a call that would otherwise block for five or more.
/// These are local loopback sockets. They say nothing about adb or USB, whose blocking behaviour
/// is device test V14.
/// </summary>
public sealed class ConnectionCancellationTests : IAsyncLifetime
{
    /// <summary>Far less than any timeout a cancelled call would otherwise have run into.</summary>
    private const long Prompt = 1_000;

    private readonly LoopbackTransport _transport = new();
    private readonly List<IDisposable> _cleanup = [];
    private readonly HostConnectionController _controller;

    public ConnectionCancellationTests()
    {
        // Long enough that only cancellation can explain a quick return.
        _controller = new HostConnectionController(_transport, new HostConnectionOptions
        {
            HandshakeStepTimeout = TimeSpan.FromSeconds(30),
            KeepaliveTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _controller.CloseAsync("the test is over", null, CancellationToken.None).Within();
        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

        _transport.Dispose();
    }

    [Fact]
    public async Task Cancelling_while_the_transport_is_being_set_up_is_reported_as_cancelled()
    {
        _transport.Prepare = (_, token) => Wait.UntilCancelled<TransportEndpoint>(token);
        using var cancel = new CancellationTokenSource();
        Task<AttemptOutcome> open = _controller.OpenAsync(cancel.Token);

        AttemptOutcome outcome = await CancelAndAwait(cancel, open);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task Cancelling_a_connect_that_is_waiting_ends_it_promptly_and_leaves_no_socket()
    {
        // A loopback port with nothing listening. On Windows a connect to it is retried by the
        // stack for about two seconds before it is refused, which is a real blocked connect.
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        _transport.Prepare = (_, _) => Task.FromResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(new IPEndPoint(IPAddress.Loopback, port))));
        using var cancel = new CancellationTokenSource();
        Task<AttemptOutcome> open = _controller.OpenAsync(cancel.Token);

        // Given time to be inside the connect; if the stack refused at once instead, the outcome
        // is a refusal and the test says so rather than passing for the wrong reason.
        if (await Task.WhenAny(open, Task.Delay(200, TestContext.Current.CancellationToken)) == open)
        {
            Assert.Skip($"the connect did not block on this machine ({(await open).Fault?.Code}); nothing to cancel");
        }

        AttemptOutcome outcome = await CancelAndAwait(cancel, open);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_clients_hello_closes_the_socket_and_is_reported_as_cancelled()
    {
        TestClient silent = await OpenAsync();
        using var cancel = new CancellationTokenSource();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(cancel.Token);
        await silent.ExpectAsync<HelloMessage>();

        AttemptOutcome<ClientCapabilities> outcome = await CancelAndAwait(cancel, exchange);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.Empty(await silent.ReadUntilClosedAsync());
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_clients_capabilities_closes_the_socket_and_is_reported_as_cancelled()
    {
        TestClient client = await OpenAsync();
        using var cancel = new CancellationTokenSource();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(cancel.Token);
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();

        AttemptOutcome<ClientCapabilities> outcome = await CancelAndAwait(cancel, exchange);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        await client.ReadUntilClosedAsync();
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_client_to_be_ready_closes_the_socket_and_is_reported_as_cancelled()
    {
        TestClient client = await ExchangedAsync();
        using var cancel = new CancellationTokenSource();
        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, cancel.Token);
        await client.ExpectAsync<StreamConfigMessage>();

        AttemptOutcome<PeerDescription> outcome = await CancelAndAwait(cancel, configure);

        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        await client.ReadUntilClosedAsync();
        Assert.Throws<InvalidOperationException>(() => { _ = _controller.WaitForEndAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task Closing_with_a_cancelled_token_while_the_reader_is_blocked_ends_it_promptly_without_a_goodbye()
    {
        TestClient client = await StreamingAsync();
        Task<ConnectionEnd> ended = _controller.WaitForEndAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        long took = await Loopback.TimeAsync(() => _controller.CloseAsync("stopping", null, cancelled.Token).Within());

        Assert.True(took < Prompt, $"closing took {took} ms");
        Assert.Equal(ConnectionEndKind.Closed, (await ended.Within()).Kind);
        Assert.DoesNotContain(await client.ReadUntilClosedAsync(), message => message is ByeMessage);

        // A cancelled close still releases the transport: cancellation shortens the goodbye only.
        Assert.Equal(1, _transport.Releases);
    }

    [Fact]
    public async Task Closing_while_a_write_is_blocked_on_a_client_that_stopped_reading_ends_the_write_by_closing_the_socket()
    {
        _transport.Listener.Server.ReceiveBufferSize = 8 * 1024;
        TestClient client = await StreamingAsync();
        byte[] large = HostConnectionControllerTests.LargeNonIdrAccessUnit(2 * 1024 * 1024);
        _controller.OfferVideo(H264Samples.IdrAccessUnit, 1);
        _controller.OfferVideo(large, 2);
        _controller.OfferVideo(large, 3);

        // The writer is now inside a send the client will never take.
        long took = await Loopback.TimeAsync(() => _controller.CloseAsync("stopping", null, CancellationToken.None).Within());

        Assert.True(took < Prompt, $"closing took {took} ms with a write blocked");
        Assert.Equal(0, _controller.Counters.Unaccounted);
        client.Socket.ReceiveBufferSize = 1024 * 1024;
        await client.WaitUntilClosedByHostAsync();
    }

    [Fact]
    public async Task Closing_during_a_handshake_step_that_was_not_cancelled_ends_the_step_promptly()
    {
        // The coordinator calls CloseAsync beside a step only when the step overran the teardown
        // deadline and was abandoned. The step must still end, and must not claim success.
        TestClient silent = await OpenAsync();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(CancellationToken.None);
        await silent.ExpectAsync<HelloMessage>();

        await _controller.CloseAsync("abandoned", null, CancellationToken.None).Within();
        AttemptOutcome<ClientCapabilities> outcome = await exchange.Within(TimeSpan.FromMilliseconds(Prompt));

        Assert.Equal((OutcomeKind.Failed, ConnectionFaultCodes.ClosedByHost, true), (outcome.Kind, outcome.Fault!.Code, outcome.Fault.IsRetryable));
        Assert.Contains(await silent.ReadUntilClosedAsync(), message => message is ByeMessage);
        Assert.False(_controller.HasConnection);
    }

    [Fact]
    public async Task An_open_that_completes_after_the_session_was_closed_keeps_nothing()
    {
        // The transport stands for adb answering late: after the coordinator abandoned the open
        // at its deadline and closed the session.
        var answer = new TaskCompletionSource<AttemptOutcome<TransportEndpoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.Prepare = (_, _) => answer.Task;
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);

        await _controller.CloseAsync("abandoned", null, CancellationToken.None).Within();
        answer.SetResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(_transport.EndPoint)));
        AttemptOutcome outcome = await open.Within();

        Assert.Equal((OutcomeKind.Failed, ConnectionFaultCodes.ClosedByHost), (outcome.Kind, outcome.Fault!.Code));
        Assert.False(_controller.HasConnection);

        // It did connect before finding out; that connection was closed at once.
        using TestClient orphan = await _transport.AcceptAsync();
        await orphan.WaitUntilClosedByHostAsync();
    }

    [Fact]
    public async Task A_step_that_completes_just_as_it_is_cancelled_reports_success_only_with_its_connection_intact()
    {
        // Races the client's answer against the cancellation, many times, at varying offsets. Each
        // round must end either Succeeded with a usable connection, or Cancelled with the
        // connection closed: never Failed, never a dead connection reported as success.
        int succeeded = 0;
        int cancelledCount = 0;
        for (int round = 0; round < 40; round++)
        {
            TestClient client = await OpenAsync();
            using var cancel = new CancellationTokenSource();
            Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(cancel.Token);
            await client.SendHelloAsync();
            await client.ExpectAsync<HelloMessage>();

            Task answering = client.SendAsync(Loopback.Tablet);
            if (round % 4 != 0)
            {
                await Task.Delay(round % 3, TestContext.Current.CancellationToken);
            }

            cancel.Cancel();
            await answering;
            AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

            Assert.NotEqual(OutcomeKind.Failed, outcome.Kind);
            if (outcome.Kind == OutcomeKind.Succeeded)
            {
                succeeded++;
                Assert.True(_controller.HasConnection, $"round {round}: success was reported for a connection that is not held");
                Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None);
                await client.ExpectAsync<StreamConfigMessage>();
                await client.SendAsync(new StreamReadyMessage(null, null));
                Assert.Equal(OutcomeKind.Succeeded, (await configure.Within()).Kind);
            }
            else
            {
                cancelledCount++;
                Assert.False(_controller.HasConnection);
                await client.ReadUntilClosedAsync();
            }
        }

        // Both endings must have happened for the race to have been tested at all.
        Assert.True(succeeded > 0 && cancelledCount > 0, $"{succeeded} succeeded and {cancelledCount} were cancelled");
    }

    [Fact]
    public async Task Cancelling_and_closing_at_the_same_moment_from_different_threads_never_deadlocks_or_releases_twice()
    {
        for (int round = 0; round < 20; round++)
        {
            TestClient client = await OpenAsync();
            using var cancel = new CancellationTokenSource();
            Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(cancel.Token);
            await client.ExpectAsync<HelloMessage>();

            using var start = new Barrier(2);
            Task cancelling = Task.Run(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                cancel.Cancel();
            }, TestContext.Current.CancellationToken);
            Task closing = Task.Run(async () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                await _controller.CloseAsync("both at once", null, cancel.Token);
            }, TestContext.Current.CancellationToken);

            await Task.WhenAll(cancelling, closing).Within();
            AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();

            Assert.NotEqual(OutcomeKind.Succeeded, outcome.Kind);
            Assert.False(_controller.HasConnection);
            Assert.Equal(round + 1, _transport.Releases);
            await client.ReadUntilClosedAsync();
        }
    }

    // ---- Helpers ----

    private static async Task<T> CancelAndAwait<T>(CancellationTokenSource cancel, Task<T> call)
    {
        Assert.False(call.IsCompleted, "the call finished before it could be cancelled");
        long started = Environment.TickCount64;
        cancel.Cancel();
        T result = await call.Within();
        long took = Environment.TickCount64 - started;
        Assert.True(took < Prompt, $"the cancelled call took {took} ms to return");
        return result;
    }

    private async Task<TestClient> OpenAsync()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(CancellationToken.None);
        TestClient client = await _transport.AcceptAsync();
        _cleanup.Add(client);
        Assert.Equal(OutcomeKind.Succeeded, (await open.Within()).Kind);
        return client;
    }

    private async Task<TestClient> ExchangedAsync()
    {
        TestClient client = await OpenAsync();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(CancellationToken.None);
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.Tablet);
        Assert.Equal(OutcomeKind.Succeeded, (await exchange.Within()).Kind);
        return client;
    }

    private async Task<TestClient> StreamingAsync()
    {
        TestClient client = await ExchangedAsync();
        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, CancellationToken.None);
        await client.ExpectAsync<StreamConfigMessage>();
        await client.SendAsync(new StreamReadyMessage(null, null));
        Assert.Equal(OutcomeKind.Succeeded, (await configure.Within()).Kind);
        return client;
    }
}

internal static class Wait
{
    /// <summary>A call that waits until cancelled and then reports it, as the contract requires.</summary>
    public static Task<AttemptOutcome<T>> UntilCancelled<T>(CancellationToken token)
        where T : class
    {
        var source = new TaskCompletionSource<AttemptOutcome<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => source.TrySetResult(AttemptOutcome<T>.Cancelled));
        return source.Task;
    }
}
