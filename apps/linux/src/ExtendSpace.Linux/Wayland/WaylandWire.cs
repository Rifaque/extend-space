using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ExtendSpace.Linux.Wayland;

/// <summary>
/// Encodes and decodes Wayland wire messages (D104): a header of the object id, then the message size
/// (upper 16 bits) and opcode (lower 16 bits), then 32-bit arguments; strings carry their length with
/// the terminating NUL and are padded to 4 bytes. Pure, so it is tested byte for byte.
/// </summary>
public static class WaylandWire
{
    /// <summary>Fixed-point 24.8, the protocol's <c>fixed</c> type.</summary>
    public static int Fixed(double value) => (int)Math.Round(value * 256.0);

    public sealed class MessageBuilder(uint objectId, ushort opcode)
    {
        private readonly List<byte> _arguments = [];

        public MessageBuilder Uint(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _arguments.AddRange(b.ToArray());
            return this;
        }

        public MessageBuilder Int(int value) => Uint(unchecked((uint)value));

        public MessageBuilder String(string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            Uint((uint)text.Length + 1);
            _arguments.AddRange(text);
            _arguments.Add(0);
            while (_arguments.Count % 4 != 0)
            {
                _arguments.Add(0);
            }

            return this;
        }

        public byte[] Build()
        {
            int size = 8 + _arguments.Count;
            var message = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(message, objectId);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), ((uint)size << 16) | opcode);
            _arguments.CopyTo(message, 8);
            return message;
        }
    }

    /// <summary>One received event: its object, opcode and raw argument bytes.</summary>
    public readonly record struct Event(uint ObjectId, ushort Opcode, byte[] Arguments)
    {
        public uint Uint(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Arguments.AsSpan(offset));

        /// <summary>Reads a string at <paramref name="offset"/>; <paramref name="next"/> is the offset after it.</summary>
        public string String(int offset, out int next)
        {
            int length = (int)Uint(offset);
            next = offset + 4 + ((length + 3) & ~3);
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(Arguments, offset + 4, length - 1);
        }
    }

    /// <summary>Splits complete events off the front of <paramref name="buffer"/>; returns how many bytes were used.</summary>
    public static int Parse(ReadOnlySpan<byte> buffer, List<Event> events)
    {
        int used = 0;
        while (buffer.Length - used >= 8)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buffer[used..]);
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(used + 4)..]);
            int size = (int)(word >> 16);
            if (size < 8)
            {
                throw new InvalidDataException($"Wayland message of {size} bytes");
            }

            if (buffer.Length - used < size)
            {
                break;
            }

            events.Add(new Event(id, (ushort)(word & 0xFFFF), buffer.Slice(used + 8, size - 8).ToArray()));
            used += size;
        }

        return used;
    }
}

/// <summary>
/// A minimal Wayland client connection (D104): enough of the core protocol to list globals, bind them
/// and send requests. No file descriptors are passed, so a plain Unix socket is enough; no libwayland.
/// A reader thread drains events, so the compositor never sees a full socket; handlers run on it.
/// </summary>
public sealed class WaylandConnection : IDisposable
{
    public const uint DisplayId = 1;
    private readonly Socket _socket;
    private readonly Lock _send = new();
    private readonly Dictionary<uint, Action<WaylandWire.Event>> _handlers = [];
    private readonly Lock _handlersGate = new();
    private readonly Thread _reader;
    private uint _nextId = 2;
    private volatile bool _closed;

    private WaylandConnection(Socket socket)
    {
        _socket = socket;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ExtendSpace Wayland" };
        _reader.Start();
    }

    /// <summary>The protocol error the compositor sent, if any; the connection is unusable after one.</summary>
    public string? Error { get; private set; }

    public bool IsOpen => !_closed;

    /// <summary>The socket of <c>$WAYLAND_DISPLAY</c> (a name under <c>$XDG_RUNTIME_DIR</c>, or a path); null without a session.</summary>
    public static string? SocketPath(Func<string, string?>? variable = null)
    {
        variable ??= Environment.GetEnvironmentVariable;
        string? display = variable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(display))
        {
            return null;
        }

        if (Path.IsPathRooted(display))
        {
            return display;
        }

        string? runtime = variable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtime) ? null : Path.Combine(runtime, display);
    }

    public static WaylandConnection Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WaylandConnection(socket);
    }

    public uint NewId() => Interlocked.Increment(ref _nextId) - 1;

    public void On(uint objectId, Action<WaylandWire.Event> handler)
    {
        lock (_handlersGate)
        {
            _handlers[objectId] = handler;
        }
    }

    public void Send(WaylandWire.MessageBuilder message)
    {
        byte[] bytes = message.Build();
        lock (_send)
        {
            if (_closed)
            {
                throw new IOException("The Wayland connection is closed.");
            }

            _socket.Send(bytes);
        }
    }

    /// <summary>Sends wl_display.sync and waits for its done event: every earlier event has then arrived.</summary>
    public bool Roundtrip(TimeSpan timeout)
    {
        uint callback = NewId();
        using var done = new ManualResetEventSlim();
        On(callback, _ => done.Set());
        Send(new WaylandWire.MessageBuilder(DisplayId, 0).Uint(callback));
        return done.Wait(timeout) && Error is null;
    }

    public void Dispose()
    {
        _closed = true;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }

        _socket.Dispose();
    }

    private void ReadLoop()
    {
        var buffer = new byte[65536];
        int filled = 0;
        var events = new List<WaylandWire.Event>();
        try
        {
            while (!_closed)
            {
                int read = _socket.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
                events.Clear();
                int used = WaylandWire.Parse(buffer.AsSpan(0, filled), events);
                Buffer.BlockCopy(buffer, used, buffer, 0, filled - used);
                filled -= used;
                foreach (WaylandWire.Event e in events)
                {
                    Dispatch(e);
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            _closed = true;
        }
    }

    private void Dispatch(WaylandWire.Event e)
    {
        if (e.ObjectId == DisplayId && e.Opcode == 0)
        {
            // wl_display.error(object, code, message)
            Error = $"Wayland protocol error {e.Uint(4)} on object {e.Uint(0)}: {e.String(8, out _)}";
            return;
        }

        Action<WaylandWire.Event>? handler;
        lock (_handlersGate)
        {
            _handlers.TryGetValue(e.ObjectId, out handler);
        }

        handler?.Invoke(e);
    }
}
