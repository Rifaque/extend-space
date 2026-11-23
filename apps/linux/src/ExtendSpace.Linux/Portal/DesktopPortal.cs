using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace ExtendSpace.Linux.Portal;

/// <summary>What the ScreenCast portal is asked for (D98).</summary>
/// <param name="Virtual">True: a new virtual monitor (source type VIRTUAL, Extend); false: an existing monitor the user picks (Mirror).</param>
/// <param name="RemoteControl">True: also ask for pointer and touchscreen through the RemoteDesktop portal (input not Off).</param>
public sealed record PortalRequest(bool Virtual, bool RemoteControl, string? RestoreToken = null);

/// <summary>The started stream: PipeWire node, its size, and the token that skips the dialog next time.</summary>
public sealed record PortalStream(uint NodeId, int Width, int Height, string? RestoreToken);

/// <summary>A portal answered "no" or failed; the user cancelled the dialog when <see cref="Cancelled"/>.</summary>
public sealed class PortalException(string message, bool cancelled = false) : Exception(message)
{
    public bool Cancelled { get; } = cancelled;
}

/// <summary>
/// One ScreenCast (and, with input, RemoteDesktop) portal session on the session bus (D98, D100). The
/// desktop shows its own consent dialog; ExtendSpace never bypasses it. Responses are matched by the
/// portal's well-known name on a Tmds.DBus.Protocol version with the signal-spoofing fix
/// (GHSA-xrw6-gwf8-vvr9: fixed in 0.92.0 and backported to 0.21.3, the version Avalonia 11.3 uses). Closing the session removes a virtual monitor.
/// </summary>
public sealed class DesktopPortal : IAsyncDisposable
{
    public const string Destination = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const uint SourceMonitor = 1;
    public const uint SourceVirtual = 4;
    public const uint CursorEmbedded = 2;
    public const uint DevicePointer = 2;
    public const uint DeviceTouchscreen = 4;
    public const uint PersistUntilRevoked = 2;

    private readonly Connection _connection;
    private string? _session;
    private bool _remote;
    private int _tokens;

    private DesktopPortal(Connection connection) => _connection = connection;

    public PortalStream? Stream { get; private set; }

    /// <summary>Whether the session can inject input (RemoteDesktop started with pointer/touch).</summary>
    public bool CanControl => _remote && _session is not null && Stream is not null;

    /// <summary>The source types the ScreenCast portal offers (bitmask), or null when there is no portal.</summary>
    public static async Task<uint?> AvailableSourceTypesAsync(CancellationToken cancellation)
    {
        try
        {
            using var connection = new Connection(Address.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Destination, path: ObjectPath, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss", flags: MessageFlags.None);
                writer.WriteString("org.freedesktop.portal.ScreenCast");
                writer.WriteString("AvailableSourceTypes");
                message = writer.CreateMessage();
            }

            return await connection.CallMethodAsync(message, (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32(), null).WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Asks the desktop for a stream (shows its dialog unless the restore token is accepted).</summary>
    public static async Task<DesktopPortal> StartAsync(PortalRequest request, CancellationToken cancellation)
    {
        var connection = new Connection(Address.Session ?? throw new PortalException("There is no D-Bus session bus."));
        var portal = new DesktopPortal(connection);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            await portal.OpenAsync(request, cancellation).ConfigureAwait(false);
            return portal;
        }
        catch
        {
            await portal.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The PipeWire remote for the stream, as a file descriptor the pipeline reads.</summary>
    public async Task<SafeFileHandle> OpenPipeWireRemoteAsync(CancellationToken cancellation)
    {
        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Destination, path: ObjectPath, @interface: "org.freedesktop.portal.ScreenCast", member: "OpenPipeWireRemote", signature: "oa{sv}", flags: MessageFlags.None);
            writer.WriteObjectPath(_session!);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            message = writer.CreateMessage();
        }

        return await _connection.CallMethodAsync(message, (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()!, null).WaitAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Absolute pointer motion in stream coordinates (pixels of the stream).</summary>
    public void PointerMotion(double x, double y) => Notify("NotifyPointerMotionAbsolute", "oa{sv}udd", w => { w.WriteUInt32(Stream!.NodeId); w.WriteDouble(x); w.WriteDouble(y); });

    /// <summary>Linux evdev button codes: BTN_LEFT 0x110, BTN_RIGHT 0x111.</summary>
    public void PointerButton(int evdevButton, bool pressed) => Notify("NotifyPointerButton", "oa{sv}iu", w => { w.WriteInt32(evdevButton); w.WriteUInt32(pressed ? 1u : 0u); });

    /// <summary>Discrete wheel steps; axis 0 vertical, 1 horizontal.</summary>
    public void PointerAxisDiscrete(uint axis, int steps) => Notify("NotifyPointerAxisDiscrete", "oa{sv}ui", w => { w.WriteUInt32(axis); w.WriteInt32(steps); });

    public void TouchDown(uint slot, double x, double y) => Notify("NotifyTouchDown", "oa{sv}uudd", w => { w.WriteUInt32(Stream!.NodeId); w.WriteUInt32(slot); w.WriteDouble(x); w.WriteDouble(y); });

    public void TouchMotion(uint slot, double x, double y) => Notify("NotifyTouchMotion", "oa{sv}uudd", w => { w.WriteUInt32(Stream!.NodeId); w.WriteUInt32(slot); w.WriteDouble(x); w.WriteDouble(y); });

    public void TouchUp(uint slot) => Notify("NotifyTouchUp", "oa{sv}u", w => w.WriteUInt32(slot));

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            try
            {
                MessageBuffer message;
                using (MessageWriter writer = _connection.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader(destination: Destination, path: session, @interface: "org.freedesktop.portal.Session", member: "Close", signature: null, flags: MessageFlags.None);
                    message = writer.CreateMessage();
                }

                await _connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The session goes with the connection anyway.
            }
        }

        _connection.Dispose();
    }

    private async Task OpenAsync(PortalRequest request, CancellationToken cancellation)
    {
        _remote = request.RemoteControl;
        string createInterface = _remote ? "org.freedesktop.portal.RemoteDesktop" : "org.freedesktop.portal.ScreenCast";
        Dictionary<string, VariantValue> created = await RequestAsync(createInterface, "CreateSession", null, new()
        {
            ["session_handle_token"] = $"extendspace{Environment.ProcessId}",
        }, cancellation).ConfigureAwait(false);
        _session = created.TryGetValue("session_handle", out VariantValue handle) ? handle.GetString() : throw new PortalException("The portal returned no session.");

        if (_remote)
        {
            await RequestAsync("org.freedesktop.portal.RemoteDesktop", "SelectDevices", _session, new()
            {
                ["types"] = DevicePointer | DeviceTouchscreen,
            }, cancellation).ConfigureAwait(false);
        }

        var sources = new Dictionary<string, VariantValue>
        {
            ["types"] = request.Virtual ? SourceVirtual : SourceMonitor,
            ["multiple"] = false,
            ["cursor_mode"] = CursorEmbedded,
        };
        if (!_remote)
        {
            // Persistence on a ScreenCast-only session; RemoteDesktop sessions carry it on SelectDevices in v2.
            sources["persist_mode"] = PersistUntilRevoked;
            if (request.RestoreToken is { } token)
            {
                sources["restore_token"] = token;
            }
        }

        await RequestAsync("org.freedesktop.portal.ScreenCast", "SelectSources", _session, sources, cancellation).ConfigureAwait(false);
        Dictionary<string, VariantValue> started = await RequestAsync(createInterface, "Start", _session, [], cancellation, parentWindow: string.Empty).ConfigureAwait(false);
        Stream = ParseStream(started) ?? throw new PortalException("The portal started no stream.");
    }

    /// <summary>The first stream of a Start response: <c>streams</c> is <c>a(ua{sv})</c>; <c>size</c> is <c>(ii)</c>.</summary>
    internal static PortalStream? ParseStream(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("streams", out VariantValue streams) || streams.Count == 0)
        {
            return null;
        }

        VariantValue first = streams.GetItem(0);
        uint node = first.GetItem(0).GetUInt32();
        VariantValue properties = first.GetItem(1);
        int width = 0;
        int height = 0;
        for (int i = 0; i < properties.Count; i++)
        {
            KeyValuePair<VariantValue, VariantValue> entry = properties.GetDictionaryEntry(i);
            if (entry.Key.GetString() == "size")
            {
                VariantValue size = entry.Value.Type == VariantValueType.Variant ? entry.Value.GetVariantValue() : entry.Value;
                width = size.GetItem(0).GetInt32();
                height = size.GetItem(1).GetInt32();
            }
        }

        string? restore = results.TryGetValue("restore_token", out VariantValue token) ? token.GetString() : null;
        return new PortalStream(node, width, height, restore);
    }

    private void Notify(string member, string signature, Action<MessageWriter> arguments)
    {
        if (!CanControl)
        {
            return;
        }

        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Destination, path: ObjectPath, @interface: "org.freedesktop.portal.RemoteDesktop", member: member, signature: signature, flags: MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(_session!);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            arguments(writer);
            message = writer.CreateMessage();
        }

        _connection.TrySendMessage(message);
    }

    /// <summary>
    /// Calls a portal method whose answer comes as a Request.Response signal. The handle token makes
    /// the request path predictable, so the match is in place before the call (no race).
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> RequestAsync(string @interface, string member, string? session, Dictionary<string, VariantValue> options, CancellationToken cancellation, string? parentWindow = null)
    {
        string token = $"extendspace{Environment.ProcessId}_{Interlocked.Increment(ref _tokens)}";
        options["handle_token"] = token;
        string sender = _connection.UniqueName!.TrimStart(':').Replace('.', '_');
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Destination,
            Path = requestPath,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
        };
        using IDisposable subscription = await _connection.AddMatchAsync(
            rule,
            (Message m, object? _) =>
            {
                Reader reader = m.GetBodyReader();
                uint code = reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Exception? e, (uint, Dictionary<string, VariantValue>) value, object? _, object? _) =>
            {
                // An exception means the connection ended; never treat it as an answer.
                if (e is not null)
                {
                    response.TrySetException(new PortalException($"The desktop portal request ended: {e.Message}"));
                }
                else
                {
                    response.TrySetResult(value);
                }
            },
            null,
            null,
            false,
            ObserverFlags.EmitOnConnectionDispose).ConfigureAwait(false);

        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            string signature = (session is null ? string.Empty : "o") + (parentWindow is null ? string.Empty : "s") + "a{sv}";
            writer.WriteMethodCallHeader(destination: Destination, path: ObjectPath, @interface: @interface, member: member, signature: signature, flags: MessageFlags.None);
            if (session is not null)
            {
                writer.WriteObjectPath(session);
            }

            if (parentWindow is not null)
            {
                writer.WriteString(parentWindow);
            }

            writer.WriteDictionary(options);
            message = writer.CreateMessage();
        }

        await _connection.CallMethodAsync(message).WaitAsync(cancellation).ConfigureAwait(false);
        (uint code, Dictionary<string, VariantValue> results) = await response.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellation).ConfigureAwait(false);
        return code switch
        {
            0 => results,
            1 => throw new PortalException("The request was cancelled in the desktop's dialog.", cancelled: true),
            _ => throw new PortalException($"The desktop refused {member} (response {code})."),
        };
    }
}
