using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using ExtendSpace.Core.Protocol;

namespace ExtendSpace.Core.Wireless;

/// <summary>
/// The arithmetic of the pairing ceremony (D77, transport architecture section 5). Shared byte-exact
/// with the Android app through <c>tests/pairing-vectors/</c>.
/// </summary>
public static class PairingCode
{
    private static ReadOnlySpan<byte> CommitLabel => "ExtendSpace pair commit v1\0"u8;

    private static ReadOnlySpan<byte> CodeLabel => "ExtendSpace pair sas v1\0"u8;

    public const int NonceLength = 32;

    /// <summary>SHA-256(label ‖ host nonce): what the PC sends before it has seen the tablet's nonce.</summary>
    public static byte[] Commit(ReadOnlySpan<byte> hostNonce)
    {
        Require(hostNonce, nameof(hostNonce));
        return SHA256.HashData([.. CommitLabel, .. hostNonce]);
    }

    /// <summary>The six digits both screens show, from both nonces and both device IDs as this side saw them.</summary>
    public static string Compute(ReadOnlySpan<byte> hostNonce, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> hostId, ReadOnlySpan<byte> clientId)
    {
        Require(hostNonce, nameof(hostNonce));
        Require(clientNonce, nameof(clientNonce));
        Require(hostId, nameof(hostId));
        Require(clientId, nameof(clientId));
        byte[] digest = SHA256.HashData([.. CodeLabel, .. hostNonce, .. clientNonce, .. hostId, .. clientId]);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"123 456", as both apps display it.</summary>
    public static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private static void Require(ReadOnlySpan<byte> value, string name)
    {
        if (value.Length != NonceLength)
        {
            throw new ArgumentException($"{name} must be {NonceLength} bytes.", name);
        }
    }
}

public enum PairingStatus
{
    Paired,

    /// <summary>The user at this PC chose not to pair (the codes differed, or they cancelled).</summary>
    DeclinedHere,

    /// <summary>The tablet's user declined.</summary>
    DeclinedOnTablet,

    /// <summary>The tablet is not in pairing mode: its user has to tap Pair with a PC first.</summary>
    NotOpenOnTablet,

    Failed,
    Cancelled,
}

public sealed record PairingResult(PairingStatus Status, string Message, TrustedDevice? Device = null);

/// <summary>Shows <paramref name="code"/> to the user and returns whether they confirmed it matches the tablet's.</summary>
public delegate Task<bool> PairingConfirmation(string code, string deviceName, CancellationToken cancellation);

public sealed record PairingOptions
{
    public string DeviceName { get; init; } = Environment.MachineName;

    public string AppVersion { get; init; } = "0.0.0";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan StepTimeout { get; init; } = WireFormat.HandshakeStepTimeout;

    /// <summary>How long both users have to compare and confirm.</summary>
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// The PC's side of pairing (D77): connect, TLS without a pin, commit, reveal, show the code, and
/// store the tablet only if both users accept. One attempt per call; nothing is retried.
/// </summary>
public sealed class WirelessPairing(HostIdentity identity, TrustedDeviceStore trust, PairingOptions? options = null, Action<string>? log = null)
{
    private readonly PairingOptions _options = options ?? new PairingOptions();

    public async Task<PairingResult> PairAsync(IPEndPoint endPoint, string advertisedName, PairingConfirmation confirm, CancellationToken cancellation)
    {
        using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using CancellationTokenRegistration closeOnAbort = abort.Token.Register(socket.Dispose);
        try
        {
            using (var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(abort.Token))
            {
                connectLimit.CancelAfter(_options.ConnectTimeout);
                await socket.ConnectAsync(endPoint, connectLimit.Token).ConfigureAwait(false);
            }

            using var stepLimit = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
            stepLimit.CancelAfter(_options.StepTimeout);
            (System.Net.Security.SslStream tls, string tabletId) = await WirelessTls.ConnectAsync(
                new NetworkStream(socket, ownsSocket: false), identity, WirelessTls.PairingProtocol, expectedPeerId: null, stepLimit.Token).ConfigureAwait(false);
            await using (tls.ConfigureAwait(false))
            {
                var channel = new FramedChannel(tls);
                await channel.WriteAsync(
                    [.. WireFormat.Preface, .. ControlCodec.Encode(new HelloMessage(WireFormat.MajorVersion, WireFormat.MajorVersion, "host", _options.DeviceName, _options.AppVersion, "pair"))],
                    abort.Token).ConfigureAwait(false);

                object first = await channel.ReadControlAsync(_options.StepTimeout, abort.Token).ConfigureAwait(false);
                if (Refusal(first) is { } refused)
                {
                    return refused;
                }

                if (first is not HelloMessage { Role: "client" } hello)
                {
                    return Fail("The tablet did not answer as ExtendSpace.");
                }

                string name = hello.DeviceName.Length > 0 ? hello.DeviceName : advertisedName;
                byte[] hostNonce = RandomNumberGenerator.GetBytes(PairingCode.NonceLength);
                await channel.WriteAsync(ControlCodec.Encode(new PairCommitMessage(PairingCode.Commit(hostNonce))), abort.Token).ConfigureAwait(false);

                object answer = await channel.ReadControlAsync(_options.StepTimeout, abort.Token).ConfigureAwait(false);
                if (Refusal(answer) is { } refusedAfterCommit)
                {
                    return refusedAfterCommit;
                }

                if (answer is not PairNonceMessage nonce)
                {
                    return Fail("The tablet broke the pairing exchange.");
                }

                await channel.WriteAsync(ControlCodec.Encode(new PairRevealMessage(hostNonce)), abort.Token).ConfigureAwait(false);
                string code = PairingCode.Compute(hostNonce, nonce.Nonce, DeviceIds.ToBytes(identity.DeviceId), DeviceIds.ToBytes(tabletId));

                // Both users decide in parallel; each side says what its user chose as soon as it is chosen.
                using var decisionLimit = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                decisionLimit.CancelAfter(_options.DecisionTimeout);
                Task<object> peerTask = ReadResultAsync(channel, decisionLimit.Token);
                Task<bool> localTask = confirm(code, name, decisionLimit.Token);

                if (await Task.WhenAny(peerTask, localTask).ConfigureAwait(false) == peerTask
                    && (!peerTask.IsCompletedSuccessfully || peerTask.Result is not PairResultMessage { Accepted: true }))
                {
                    // The tablet declined, ended or went away first: the question here is withdrawn.
                    await decisionLimit.CancelAsync().ConfigureAwait(false);
                    object ended = await peerTask.ConfigureAwait(false);
                    return Refusal(ended) ?? new PairingResult(PairingStatus.DeclinedOnTablet, $"{name} declined the pairing.");
                }

                bool accepted;
                try
                {
                    accepted = await localTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    return peerTask.IsCompletedSuccessfully && Refusal(peerTask.Result) is { } gone
                        ? gone
                        : new PairingResult(PairingStatus.Failed, "Nobody confirmed the code in time.");
                }

                await channel.WriteAsync(ControlCodec.Encode(new PairResultMessage(accepted)), abort.Token).ConfigureAwait(false);
                if (!accepted)
                {
                    // Close gracefully: closing a socket with the tablet's own answer still unread makes the
                    // kernel reset the connection (Linux does so immediately), and a reset can discard the
                    // "declined" just sent before the tablet reads it.
                    await CloseQuietlyAsync(tls, peerTask).ConfigureAwait(false);
                    return new PairingResult(PairingStatus.DeclinedHere, "Pairing was cancelled on this PC.");
                }

                object peer = await peerTask.ConfigureAwait(false);
                if (peer is not PairResultMessage { Accepted: true })
                {
                    return Refusal(peer) ?? new PairingResult(PairingStatus.DeclinedOnTablet, $"{name} declined the pairing.");
                }

                var device = new TrustedDevice(tabletId, name, DateTimeOffset.UtcNow, endPoint.Address.ToString(), endPoint.Port);
                trust.Add(device);
                log?.Invoke($"paired with tablet {DeviceIds.Short(tabletId)}");
                try
                {
                    await channel.WriteAsync(ControlCodec.Encode(new ByeMessage("paired")), abort.Token).ConfigureAwait(false);
                    await tls.ShutdownAsync().WaitAsync(TimeSpan.FromMilliseconds(500), abort.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    // Trust is already stored on both sides; a lost goodbye changes nothing.
                }

                return new PairingResult(PairingStatus.Paired, $"Paired with {name}.", device);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new PairingResult(PairingStatus.Cancelled, "Pairing was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return Fail("The tablet did not answer in time.");
        }
        catch (TimeoutException)
        {
            return Fail("The tablet did not answer in time.");
        }
        catch (ProtocolException violation)
        {
            return Fail($"The pairing exchange was broken ({violation.Code}).");
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException or ObjectDisposedException)
        {
            log?.Invoke($"pairing failed: {exception.GetType().Name}");
            return Fail("The connection to the tablet failed. Check that both devices are on the same network and the tablet is in pairing mode.");
        }
    }

    /// <summary>Sends close_notify and reads what the tablet still sends (its answer, its close), briefly.</summary>
    private static async Task CloseQuietlyAsync(System.Net.Security.SslStream tls, Task<object> peerTask)
    {
        try
        {
            await tls.ShutdownAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            await peerTask.WaitAsync(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or ProtocolException or SocketException)
        {
            // The decline was written; a peer that is already gone changes nothing.
        }
    }

    private static async Task<object> ReadResultAsync(FramedChannel channel, CancellationToken cancellation)
    {
        while (true)
        {
            object message = await channel.ReadControlAsync(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
            if (message is PairResultMessage or ErrorMessage or ByeMessage)
            {
                return message;
            }
        }
    }

    private static PairingResult? Refusal(object message) => message switch
    {
        ErrorMessage { Code: WireErrorCode.PairingNotOpen } => new PairingResult(PairingStatus.NotOpenOnTablet, "The tablet is not in pairing mode. On the tablet, tap Pair with a PC, then try again."),
        ErrorMessage { Code: WireErrorCode.PairingFailed } => new PairingResult(PairingStatus.Failed, "The tablet rejected the exchange. Try again."),
        ErrorMessage error => new PairingResult(PairingStatus.Failed, $"The tablet refused: {error.Code}."),
        ByeMessage => new PairingResult(PairingStatus.DeclinedOnTablet, "The tablet ended the pairing."),
        _ => null,
    };

    private static PairingResult Fail(string message) => new(PairingStatus.Failed, message);
}

/// <summary>
/// Frames on a stream outside a session connection: the pairing exchange's reads and writes. One
/// reader and one writer at a time. Unknown control types are skipped; other channels are a violation.
/// </summary>
internal sealed class FramedChannel(Stream stream)
{
    private readonly FrameParser _parser = new();
    private readonly Queue<(FrameHeader Header, byte[] Payload)> _frames = new();
    private readonly byte[] _buffer = new byte[16 * 1024];

    public Task WriteAsync(byte[] bytes, CancellationToken cancellation) => stream.WriteAsync(bytes, cancellation).AsTask();

    /// <exception cref="TimeoutException">Nothing arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="EndOfStreamException">The peer closed the connection.</exception>
    public async Task<object> ReadControlAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            limit.CancelAfter(timeout);
        }

        while (true)
        {
            while (_frames.Count > 0)
            {
                (FrameHeader header, byte[] payload) = _frames.Dequeue();
                if (header.Channel != WireFormat.ChannelControl)
                {
                    throw new ProtocolException(WireErrorCode.UnexpectedMessage, $"A pairing connection carries control messages only; channel {header.Channel} arrived.");
                }

                if (ControlCodec.Decode(header.Type, payload) is { } message)
                {
                    return message;
                }
            }

            int read;
            try
            {
                read = await stream.ReadAsync(_buffer, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("Nothing arrived in time.");
            }

            if (read == 0)
            {
                throw new EndOfStreamException("The peer closed the connection.");
            }

            _parser.Append(_buffer.AsSpan(0, read), (header, payload) => _frames.Enqueue((header, payload.ToArray())));
        }
    }
}
