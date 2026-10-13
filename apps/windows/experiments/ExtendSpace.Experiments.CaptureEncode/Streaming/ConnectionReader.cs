using System.Net.Sockets;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <param name="ReceivedMicros">The host clock when the bytes that completed this frame were read.</param>
internal readonly record struct ReceivedFrame(FrameHeader Header, byte[] Payload, long ReceivedMicros);

/// <summary>
/// Reads frames from a socket. It waits with <see cref="Socket.Poll(int, SelectMode)"/> rather than
/// a receive timeout, so that giving up on a wait never disturbs the connection, and it can be
/// interrupted promptly.
/// </summary>
internal sealed class ConnectionReader(Socket socket)
{
    private const int PollMicroseconds = 50_000;

    private readonly FrameParser _parser = new();
    private readonly Queue<ReceivedFrame> _frames = new();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private long _bytesReceived;

    /// <summary>
    /// Returns the next frame, or false if none arrived within <paramref name="timeout"/> or the
    /// wait was cancelled.
    /// </summary>
    /// <exception cref="IOException">The other side closed the connection.</exception>
    /// <exception cref="ProtocolException">What arrived is not a valid ExtendSpace stream.</exception>
    public bool TryNext(TimeSpan timeout, CancellationToken cancellation, out ReceivedFrame frame)
    {
        long deadline = HostClock.NowMicros + (long)timeout.TotalMicroseconds;
        while (_frames.Count == 0)
        {
            if (cancellation.IsCancellationRequested || HostClock.NowMicros >= deadline)
            {
                frame = default;
                return false;
            }

            if (!socket.Poll(PollMicroseconds, SelectMode.SelectRead))
            {
                continue;
            }

            int read = socket.Receive(_buffer);
            if (read == 0)
            {
                throw new IOException(_bytesReceived == 0
                    ? "the connection was closed before anything arrived; behind an adb port forward that means no app is listening"
                    : _parser.IsAtFrameBoundary
                        ? "the other side closed the connection"
                        : "the other side closed the connection in the middle of a message");
            }

            _bytesReceived += read;

            long receivedMicros = HostClock.NowMicros;
            _parser.Append(_buffer.AsSpan(0, read), (header, payload) => _frames.Enqueue(new ReceivedFrame(header, payload.ToArray(), receivedMicros)));
        }

        frame = _frames.Dequeue();
        return true;
    }

    /// <summary>Handshake step: the next message must be a <typeparamref name="T"/>.</summary>
    /// <exception cref="TimeoutException">It did not arrive in time.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    /// <exception cref="ProtocolException">Something else arrived, or the client reported an error.</exception>
    public T Expect<T>(TimeSpan timeout, CancellationToken cancellation = default)
        where T : class
    {
        if (!TryNext(timeout, cancellation, out ReceivedFrame frame))
        {
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"the client did not send its {typeof(T).Name} message within {timeout.TotalSeconds:0.#} s");
        }

        if (frame.Header.Channel != Protocol.ChannelControl)
        {
            throw new ProtocolException(ErrorCode.UnexpectedMessage, $"Expected a {typeof(T).Name} message, received a frame on channel {frame.Header.Channel}.");
        }

        return ControlCodec.Decode(frame.Header.Type, frame.Payload) switch
        {
            T expected => expected,
            ErrorMessage error => throw new ProtocolException(error.Code, $"the client reported: {error.Message}"),
            Bye bye => throw new IOException($"the client ended the session during the handshake: {bye.Reason}"),
            _ => throw new ProtocolException(
                ErrorCode.UnexpectedMessage,
                $"Expected a {typeof(T).Name} message, received control type {frame.Header.Type}."),
        };
    }
}
