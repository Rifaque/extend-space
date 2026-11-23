using System.Net.Sockets;
using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Transport;

/// <summary>
/// The session's transport, chosen per session (D75): USB or Wi-Fi as the Connect screen set it in
/// <see cref="HostDeviceSelection"/>. The choice is read at the session's first attempt and kept until
/// <see cref="ReleaseAsync"/>, so that a change on the screen never moves a running session.
/// </summary>
public sealed class RoutedTransport(HostDeviceSelection selection, IHostTransport usb, IHostTransport? wireless) : IHostTransport
{
    private IHostTransport? _current;

    public TransportKind Kind => (_current ?? Choose()).Kind;

    public Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation)
    {
        _current ??= Choose();
        return _current.PrepareAsync(cancellation);
    }

    public Task<AttemptOutcome<Stream>> SecureAsync(Socket connected, CancellationToken cancellation) =>
        (_current ?? throw new InvalidOperationException("PrepareAsync has not been called.")).SecureAsync(connected, cancellation);

    public async Task ReleaseAsync(CancellationToken cancellation)
    {
        IHostTransport? current = _current;
        _current = null;
        if (current is not null)
        {
            await current.ReleaseAsync(cancellation).ConfigureAwait(false);
        }
    }

    private IHostTransport Choose() => selection.Transport == TransportKind.Wireless
        ? wireless ?? throw new InvalidOperationException("This host was built without a wireless transport.")
        : usb;
}
