using System.Net;
using System.Net.Sockets;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;

namespace ExtendSpace.Core.Tests.Connection;

// A stand-in for adb's forwarded port and the Android app behind it, on a real loopback socket.
// The controller under test connects, reads and writes exactly as it would through adb; what these
// cannot show is anything about adb, USB or the tablet. Every wait is bounded by Patience, a
// failsafe that fails the test rather than hanging it; none is a sleep that a test relies on.

internal static class Loopback
{
    /// <summary>The failsafe on every wait. A passing test never comes near it.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public static readonly CapabilitiesMessage Tablet = new(3200, 2136, 144, [new DecoderEntry("c2.fake.avc.decoder", true, true, 4096, 4096)]);

    public static readonly CapabilitiesMessage TinyDecoder = new(800, 600, 60, [new DecoderEntry("c2.fake.small", false, false, 640, 480)]);

    public static readonly StreamSettings Stream = new(1920, 1080, 60, 12_000_000, "Test Encoder", true);

    /// <summary>Awaits <paramref name="task"/>, failing the test if it takes longer than <see cref="Patience"/>.</summary>
    public static async Task<T> Within<T>(this Task<T> task, TimeSpan? bound = null) => await task.WaitAsync(bound ?? Patience);

    public static async Task Within(this Task task, TimeSpan? bound = null) => await task.WaitAsync(bound ?? Patience);

    /// <summary>How long <paramref name="action"/> took, in milliseconds.</summary>
    public static async Task<long> TimeAsync(Func<Task> action)
    {
        long started = Environment.TickCount64;
        await action();
        return Environment.TickCount64 - started;
    }
}

/// <summary>
/// The transport seam over a loopback listener that plays adb's forwarded port. By default each
/// prepare succeeds with the listener's endpoint; a test can script it.
/// </summary>
internal sealed class LoopbackTransport : IHostTransport, IDisposable
{
    private int _prepares;
    private int _releases;

    public LoopbackTransport()
    {
        Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        EndPoint = (IPEndPoint)Listener.LocalEndpoint;
    }

    public TcpListener Listener { get; }

    public IPEndPoint EndPoint { get; }

    /// <summary>Replaces the default reply; given the attempt number (from 1).</summary>
    public Func<int, CancellationToken, Task<AttemptOutcome<TransportEndpoint>>>? Prepare { get; set; }

    public Func<CancellationToken, Task>? Release { get; set; }

    public int Prepares => Volatile.Read(ref _prepares);

    public int Releases => Volatile.Read(ref _releases);

    public CancellationToken LastReleaseToken { get; private set; }

    public Task<AttemptOutcome<TransportEndpoint>> PrepareAsync(CancellationToken cancellation)
    {
        int attempt = Interlocked.Increment(ref _prepares);
        return Prepare?.Invoke(attempt, cancellation) ?? Task.FromResult(AttemptOutcome<TransportEndpoint>.Succeeded(new TransportEndpoint(EndPoint)));
    }

    public Task ReleaseAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref _releases);
        LastReleaseToken = cancellation;
        return Release?.Invoke(cancellation) ?? Task.CompletedTask;
    }

    /// <summary>The next connection the host makes.</summary>
    public async Task<TestClient> AcceptAsync() => new(await Listener.AcceptSocketAsync().Within());

    public void Dispose() => Listener.Stop();
}

/// <summary>The Android app's side of the protocol, reduced to what these tests need.</summary>
internal sealed class TestClient(Socket socket, Stream? stream = null) : IDisposable
{
    // Over a plain socket unless a stream (TLS, for the Wi-Fi tests) is given; the socket path is
    // kept exactly as it was so that the USB tests see the same exceptions.
    private readonly FrameParser _parser = new();
    private readonly Queue<(FrameHeader Header, byte[] Payload)> _frames = new();
    private readonly byte[] _buffer = new byte[64 * 1024];

    public Socket Socket => socket;

    public Stream? Stream => stream;

    public Task SendAsync(byte[] bytes) => stream is null ? socket.SendAsync(bytes).Within() : stream.WriteAsync(bytes).AsTask().Within();

    private Task<int> ReceiveAsync(TimeSpan? within = null) =>
        stream is null ? socket.ReceiveAsync(_buffer).Within(within) : stream.ReadAsync(_buffer).AsTask().Within(within);

    public Task SendAsync(object message) => SendAsync(ControlCodec.Encode(message));

    public Task SendPrefaceAsync() => SendAsync(WireFormat.Preface.ToArray());

    public Task SendHelloAsync(string name = "Fake Pad", int min = 0, int max = 0, string role = "client") =>
        SendAsync([.. WireFormat.Preface, .. ControlCodec.Encode(new HelloMessage(min, max, role, name, "9.9"))]);

    /// <summary>The whole client side of the handshake. Returns the host's hello and its stream configuration.</summary>
    public async Task<(HelloMessage Hello, StreamConfigMessage Config)> HandshakeAsync(CapabilitiesMessage? capabilities = null)
    {
        await SendHelloAsync();
        HelloMessage hello = await ExpectAsync<HelloMessage>();
        await SendAsync(capabilities ?? Loopback.Tablet);
        StreamConfigMessage config = await ExpectAsync<StreamConfigMessage>();
        await SendAsync(new StreamReadyMessage("c2.fake.avc.decoder", true));
        return (hello, config);
    }

    /// <summary>The next frame from the host. Throws <see cref="EndOfStreamException"/> if the host closed.</summary>
    public async Task<(FrameHeader Header, byte[] Payload)> NextFrameAsync(TimeSpan? within = null)
    {
        while (_frames.Count == 0)
        {
            int read = await ReceiveAsync(within);
            if (read == 0)
            {
                throw new EndOfStreamException("the host closed the connection");
            }

            _parser.Append(_buffer.AsSpan(0, read), (header, payload) => _frames.Enqueue((header, payload.ToArray())));
        }

        return _frames.Dequeue();
    }

    /// <summary>The next control message, skipping pings unless <paramref name="skipPings"/> is false.</summary>
    public async Task<object?> NextMessageAsync(bool skipPings = true)
    {
        while (true)
        {
            (FrameHeader header, byte[] payload) = await NextFrameAsync();
            if (header.Channel != WireFormat.ChannelControl)
            {
                continue;
            }

            object? message = ControlCodec.Decode(header.Type, payload);
            if (message is PingMessage && skipPings)
            {
                continue;
            }

            return message;
        }
    }

    public async Task<T> ExpectAsync<T>()
        where T : class
    {
        object? message = await NextMessageAsync();
        return Assert.IsType<T>(message);
    }

    /// <summary>The next video frame, skipping control messages.</summary>
    public async Task<(FrameHeader Header, uint Sequence, long Timestamp, byte[] AccessUnit)> NextVideoAsync()
    {
        while (true)
        {
            (FrameHeader header, byte[] payload) = await NextFrameAsync();
            if (header.Channel == WireFormat.ChannelVideo)
            {
                byte[] accessUnit = VideoFrameCodec.ReadVideoPayload(payload, out uint sequence, out long timestamp).ToArray();
                return (header, sequence, timestamp, accessUnit);
            }
        }
    }

    /// <summary>Reads and discards until the host closes or resets the connection. Returns the control messages seen on the way.</summary>
    public async Task<List<object>> ReadUntilClosedAsync()
    {
        var seen = new List<object>();
        try
        {
            while (true)
            {
                (FrameHeader header, byte[] payload) = await NextFrameAsync();
                if (header.Channel == WireFormat.ChannelControl && ControlCodec.Decode(header.Type, payload) is { } message and not PingMessage)
                {
                    seen.Add(message);
                }
            }
        }
        catch (EndOfStreamException)
        {
            return seen;
        }
        catch (SocketException)
        {
            return seen;
        }
        catch (IOException) when (stream is not null)
        {
            return seen;
        }
    }

    /// <summary>Waits for the host to close without reading through the frame parser (for a peer that sent garbage).</summary>
    public async Task WaitUntilClosedByHostAsync()
    {
        try
        {
            while (await ReceiveAsync() > 0)
            {
            }
        }
        catch (SocketException)
        {
        }
        catch (IOException) when (stream is not null)
        {
        }
    }

    /// <summary>Drops the connection without a goodbye, as a pulled cable or a killed app would.</summary>
    public void CloseAbruptly() => socket.Close(0);

    /// <summary>Ends the connection at the socket level in an orderly way, so that what was sent arrives first.</summary>
    public void CloseAfterSending()
    {
        socket.Shutdown(SocketShutdown.Send);
        socket.Close(1);
    }

    public void Dispose()
    {
        stream?.Dispose();
        socket.Dispose();
    }
}
