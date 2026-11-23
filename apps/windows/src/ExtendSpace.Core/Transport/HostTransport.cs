using System.Net;
using System.Net.Sockets;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Transport;

/// <summary>Where to connect for one attempt: a loopback endpoint that reaches the device's app.</summary>
public sealed record TransportEndpoint(IPEndPoint EndPoint);

/// <summary>
/// The route to the device, under the connection controller (architecture section 3.1, the
/// "transport seam" that decision D23 deferred to stage 3). For USB it is adb and one port forward.
///
/// It follows the rules of the session contracts: one call is one attempt, nothing here retries,
/// cancellation is reported as <see cref="OutcomeKind.Cancelled"/> and ends what the call is waiting
/// on, and failures carry a fault that says whether trying again can help.
/// </summary>
public interface IHostTransport
{
    /// <summary>
    /// Sets the route up, or sets it up again after the device went away, and says where to connect.
    /// Called before every connection attempt; asking again while the route is in place changes
    /// nothing. What it sets up belongs to the session until <see cref="ReleaseAsync"/>, including
    /// anything a cancelled call may have set up before it was stopped.
    /// </summary>
    Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation);

    /// <summary>
    /// Turns the socket the controller has just connected to the endpoint into the stream the
    /// session runs over (D75). USB passes it through unchanged; Wi-Fi runs mutual TLS and checks the
    /// tablet's key against its pin. One attempt, bounded by the implementation; cancellation closes
    /// the socket. A failure caused by the peer's identity is terminal, one caused by the network is
    /// retryable. The stream never owns the socket: the controller closes the socket itself.
    /// </summary>
    Task<AttemptOutcome<Stream>> SecureAsync(Socket connected, CancellationToken cancellation) =>
        Task.FromResult(AttemptOutcome<Stream>.Succeeded(new NetworkStream(connected, ownsSocket: false)));

    /// <summary>Which route this is, for diagnostics and for the advice shown to the user.</summary>
    TransportKind Kind => TransportKind.Usb;

    /// <summary>
    /// Removes everything the session set up. Safe in any state and more than once. Bounded by the
    /// implementation; it does not stop early on <paramref name="cancellation"/>, because stopping
    /// early would leave the route in place. Throws if something could not be removed, after trying
    /// everything.
    /// </summary>
    Task ReleaseAsync(CancellationToken cancellation);
}
