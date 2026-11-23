using System.Diagnostics;

namespace ExtendSpace.Linux.Wayland;

/// <summary>
/// Mouse input on wlroots-family compositors (Hyprland, Sway) without a RemoteDesktop portal (D104):
/// the compositor's own <c>zwlr_virtual_pointer_v1</c> protocol, the mechanism wayvnc uses. It is an
/// ordinary Wayland client request: no root, no uinput, no device node. The pointer is bound to one
/// output (manager version 2), so absolute positions land on that output only, never on another
/// display. Created when a session's input starts and destroyed when it ends; any button still held
/// is released first, so nothing stays pressed.
/// </summary>
public sealed class VirtualPointer : IDisposable
{
    public const string ManagerInterface = "zwlr_virtual_pointer_manager_v1";
    private const uint BtnLeft = 0x110;
    private const uint BtnRight = 0x111;

    private readonly WaylandConnection _connection;
    private readonly uint _pointer;
    private readonly uint _manager;
    private readonly List<uint> _outputs;
    private readonly HashSet<uint> _held = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private VirtualPointer(WaylandConnection connection, uint manager, uint pointer, List<uint> outputs, string? boundOutput)
    {
        _connection = connection;
        _manager = manager;
        _pointer = pointer;
        _outputs = outputs;
        BoundOutput = boundOutput;
    }

    /// <summary>The output the pointer is confined to; null if the compositor could not bind one.</summary>
    public string? BoundOutput { get; }

    public bool IsAlive => !_disposed && _connection.IsOpen && _connection.Error is null;

    /// <summary>Whether the compositor offers the protocol (Hyprland and Sway do; GNOME and KDE do not).</summary>
    public static bool IsAvailable(string? socketPath = null)
    {
        socketPath ??= WaylandConnection.SocketPath();
        if (socketPath is null)
        {
            return false;
        }

        try
        {
            using WaylandConnection connection = WaylandConnection.Connect(socketPath);
            return Globals(connection).Any(g => g.Interface == ManagerInterface);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a pointer on <paramref name="outputName"/> (the portal stream's <c>mapping_id</c>, which is
    /// the output's name on Hyprland). Throws <see cref="InvalidOperationException"/> when the compositor
    /// cannot, or when that output is not found (rather than move the pointer over every display).
    /// </summary>
    public static VirtualPointer Create(string outputName, string? socketPath = null)
    {
        socketPath ??= WaylandConnection.SocketPath() ?? throw new InvalidOperationException("There is no Wayland session.");
        WaylandConnection connection = WaylandConnection.Connect(socketPath);
        try
        {
            List<(uint Name, string Interface, uint Version)> globals = Globals(connection, out uint registry);
            (uint Name, string Interface, uint Version) manager = globals.FirstOrDefault(g => g.Interface == ManagerInterface);
            if (manager.Interface is null)
            {
                throw new InvalidOperationException("This desktop does not offer the virtual-pointer protocol.");
            }

            (uint Name, string Interface, uint Version) seat = globals.FirstOrDefault(g => g.Interface == "wl_seat");
            uint seatId = seat.Interface is null ? 0 : Bind(connection, registry, seat.Name, "wl_seat", 1);

            // wl_output v4 announces each output's name; bind all, then pick the one asked for.
            var names = new Dictionary<uint, string>();
            var outputs = new List<uint>();
            foreach ((uint name, _, uint version) in globals.Where(g => g.Interface == "wl_output"))
            {
                if (version < 4)
                {
                    continue;
                }

                // The handler is in place before the bind: the reader thread may dispatch the name at once.
                uint id = connection.NewId();
                outputs.Add(id);
                connection.On(id, e =>
                {
                    if (e.Opcode == 4)
                    {
                        lock (names)
                        {
                            names[id] = e.String(0, out _);
                        }
                    }
                });
                Bind(connection, registry, name, "wl_output", 4, id);
            }

            connection.Roundtrip(TimeSpan.FromSeconds(2));
            uint? output;
            string known;
            lock (names)
            {
                output = names.FirstOrDefault(pair => pair.Value == outputName) is { Value: not null } found ? found.Key : null;
                known = string.Join(", ", names.Values);
            }

            if (output is null)
            {
                throw new InvalidOperationException($"The output {outputName} was not found (the desktop has: {known}).");
            }

            if (manager.Version < 2)
            {
                throw new InvalidOperationException("The desktop's virtual-pointer protocol cannot be limited to one output (version 1).");
            }

            uint managerId = Bind(connection, registry, manager.Name, ManagerInterface, 2);
            uint pointer = connection.NewId();
            // create_virtual_pointer_with_output(seat, output, id)
            connection.Send(new WaylandWire.MessageBuilder(managerId, 2).Uint(seatId).Uint(output.Value).Uint(pointer));
            if (!connection.Roundtrip(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException(connection.Error ?? "The compositor did not answer.");
            }

            return new VirtualPointer(connection, managerId, pointer, outputs, outputName);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Absolute position on the bound output, in a <paramref name="width"/> × <paramref name="height"/> space.</summary>
    public void MoveTo(int x, int y, int width, int height)
    {
        Send(new WaylandWire.MessageBuilder(_pointer, 1).Uint(Now()).Uint((uint)Math.Clamp(x, 0, width - 1)).Uint((uint)Math.Clamp(y, 0, height - 1)).Uint((uint)width).Uint((uint)height));
        Frame();
    }

    public void Button(bool right, bool pressed)
    {
        uint button = right ? BtnRight : BtnLeft;
        lock (_gate)
        {
            if (pressed ? !_held.Add(button) : !_held.Remove(button))
            {
                return;
            }
        }

        Send(new WaylandWire.MessageBuilder(_pointer, 2).Uint(Now()).Uint(button).Uint(pressed ? 1u : 0u));
        Frame();
    }

    /// <summary>Wheel notches; <paramref name="axis"/> 0 vertical (positive scrolls down), 1 horizontal (positive right).</summary>
    public void Scroll(uint axis, int notches)
    {
        if (notches == 0)
        {
            return;
        }

        uint now = Now();
        Send(new WaylandWire.MessageBuilder(_pointer, 5).Uint(0)); // axis_source wheel
        Send(new WaylandWire.MessageBuilder(_pointer, 7).Uint(now).Uint(axis).Int(WaylandWire.Fixed(15.0 * notches)).Int(notches));
        Frame();
    }

    /// <summary>
    /// Smooth scrolling as a touchpad does (D107): source "finger", values in logical pixels, positive
    /// scrolls down / right. Applications scroll by exactly this much; <see cref="ScrollStop"/> ends it.
    /// </summary>
    public void ScrollSmooth(double down, double right)
    {
        uint now = Now();
        Send(new WaylandWire.MessageBuilder(_pointer, 5).Uint(1)); // axis_source finger
        if (down != 0)
        {
            Send(new WaylandWire.MessageBuilder(_pointer, 3).Uint(now).Uint(0).Int(WaylandWire.Fixed(down)));
        }

        if (right != 0)
        {
            Send(new WaylandWire.MessageBuilder(_pointer, 3).Uint(now).Uint(1).Int(WaylandWire.Fixed(right)));
        }

        Frame();
    }

    /// <summary>The fingers lifted: axis_stop on both axes, so applications may start kinetic scrolling.</summary>
    public void ScrollStop()
    {
        uint now = Now();
        Send(new WaylandWire.MessageBuilder(_pointer, 5).Uint(1));
        Send(new WaylandWire.MessageBuilder(_pointer, 6).Uint(now).Uint(0));
        Send(new WaylandWire.MessageBuilder(_pointer, 6).Uint(now).Uint(1));
        Frame();
    }

    public void Dispose()
    {
        uint[] held;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            held = [.. _held];
            _held.Clear();
        }

        try
        {
            foreach (uint button in held)
            {
                _connection.Send(new WaylandWire.MessageBuilder(_pointer, 2).Uint(Now()).Uint(button).Uint(0));
            }

            if (held.Length > 0)
            {
                _connection.Send(new WaylandWire.MessageBuilder(_pointer, 4));
            }

            _connection.Send(new WaylandWire.MessageBuilder(_pointer, 8)); // destroy
            foreach (uint output in _outputs)
            {
                _connection.Send(new WaylandWire.MessageBuilder(output, 0)); // wl_output.release (v3+)
            }

            _connection.Send(new WaylandWire.MessageBuilder(_manager, 1)); // manager destroy
            _connection.Roundtrip(TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // The compositor went away; it dropped the device with the connection.
        }
        finally
        {
            _connection.Dispose();
        }
    }

    private void Frame() => Send(new WaylandWire.MessageBuilder(_pointer, 4));

    private void Send(WaylandWire.MessageBuilder message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _connection.Send(message);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // IsAlive turns false; the injector makes a new pointer for the next gesture.
        }
    }

    private static uint Now() => (uint)(Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);

    private static List<(uint Name, string Interface, uint Version)> Globals(WaylandConnection connection) => Globals(connection, out _);

    private static List<(uint Name, string Interface, uint Version)> Globals(WaylandConnection connection, out uint registry)
    {
        var globals = new List<(uint, string, uint)>();
        registry = connection.NewId();
        connection.On(registry, e =>
        {
            if (e.Opcode == 0)
            {
                uint name = e.Uint(0);
                string @interface = e.String(4, out int next);
                lock (globals)
                {
                    globals.Add((name, @interface, e.Uint(next)));
                }
            }
        });
        connection.Send(new WaylandWire.MessageBuilder(WaylandConnection.DisplayId, 1).Uint(registry));
        if (!connection.Roundtrip(TimeSpan.FromSeconds(2)))
        {
            throw new IOException(connection.Error ?? "The compositor did not answer.");
        }

        lock (globals)
        {
            return [.. globals];
        }
    }

    private static uint Bind(WaylandConnection connection, uint registry, uint name, string @interface, uint version, uint? newId = null)
    {
        uint id = newId ?? connection.NewId();
        connection.Send(new WaylandWire.MessageBuilder(registry, 0).Uint(name).String(@interface).Uint(version).Uint(id));
        return id;
    }
}
