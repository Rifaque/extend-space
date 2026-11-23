namespace ExtendSpace.Core.Input;

public enum ContactState
{
    Down,
    Update,
    Up,
    Cancel,
}

/// <summary>One contact of an injected touch frame, in virtual-screen pixels.</summary>
public readonly record struct InjectedContact(uint PointerId, ScreenPoint Point, ContactState State);

/// <summary>Windows' synthetic touch device (ExtendSpace.Media); a recorder in tests.</summary>
public interface ITouchInjector : IDisposable
{
    /// <summary>Injects one frame listing every active contact. False if Windows refused it.</summary>
    bool Inject(IReadOnlyList<InjectedContact> frame);
}

public enum MouseButton
{
    Left,
    Right,
}

/// <summary>Windows' mouse input (ExtendSpace.Media); a recorder in tests.</summary>
public interface IMouseInjector
{
    void Move(ScreenPoint point);

    void Button(MouseButton button, bool down);

    /// <param name="vertical">Wheel units, 120 a notch; positive scrolls the view towards the top.</param>
    /// <param name="horizontal">Positive scrolls the view towards the right.</param>
    void Wheel(int vertical, int horizontal);
}

/// <summary>Finds the mirrored monitor's rectangle now (D83).</summary>
public interface IMonitorLocator
{
    /// <param name="deviceName">Null for the main display.</param>
    ScreenRect? Locate(string? deviceName);
}

/// <summary>What the input path has done, for diagnostics.</summary>
public sealed record HostInputCounters(long FramesHandled, long FramesIgnored, long Injections, long InjectionFailures, long Releases);

/// <summary>
/// Touch from the tablet, turned into Windows input (D82–D84, input architecture sections 4–6).
///
/// <para><b>Binding.</b> Input belongs to one streaming connection: <see cref="BeginStream"/> names
/// it, frames from any other connection are ignored, and <see cref="EndStream"/>, a new stream,
/// <see cref="Mode"/> set to Off and <see cref="Dispose"/> all release every contact and button.</para>
///
/// <para><b>Threads.</b> Called on the connection's reader and on a timer; everything is under one
/// lock, and the injectors are called with it held, so that Windows sees one ordered stream of input.</para>
/// </summary>
public sealed class HostInput : IDisposable
{
    /// <summary>Held contacts are injected again this often, so that Windows does not cancel them.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    public const int TapSlopPixels = 12;
    public static readonly TimeSpan LongPressTime = TimeSpan.FromMilliseconds(600);
    public const int ScrollPixelsPerNotch = 48;

    private readonly IMonitorLocator _monitors;
    private readonly Func<ITouchInjector> _touchFactory;
    private readonly IMouseInjector _mouse;
    private readonly Func<string?> _monitorName;
    private readonly Func<long> _nowMillis;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Timer? _timer;

    // Under _gate.
    private InputMode _mode;
    private object? _owner;
    private int _frameWidth;
    private int _frameHeight;
    private uint? _lastSequence;
    private ScreenRect? _monitor;
    private ITouchInjector? _touch;
    private readonly Dictionary<byte, ScreenPoint> _active = [];
    private readonly HashSet<byte> _ignored = [];
    private long _lastInjectionMillis;
    private bool _failureLogged;
    private readonly MouseGestures _gestures;

    private long _framesHandled;
    private long _framesIgnored;
    private long _injections;
    private long _injectionFailures;
    private long _releases;

    /// <param name="autoTick">False in tests, which call <see cref="Tick"/> with their own clock.</param>
    public HostInput(
        IMonitorLocator monitors,
        Func<ITouchInjector> touchFactory,
        IMouseInjector mouse,
        Func<string?> monitorName,
        Action<string>? log = null,
        Func<long>? nowMillis = null,
        bool autoTick = true)
    {
        _monitors = monitors;
        _touchFactory = touchFactory;
        _mouse = mouse;
        _monitorName = monitorName;
        _log = log;
        _nowMillis = nowMillis ?? (() => Environment.TickCount64);
        _gestures = new MouseGestures(mouse, () => _nowMillis());
        if (autoTick)
        {
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>Raised, on any thread, when <see cref="Mode"/> changes.</summary>
    public event EventHandler? ModeChanged;

    /// <summary>
    /// Off by default (security principle 7). Turning it off releases everything at once; turning it
    /// on, or changing it, applies to the current connection only if it was offered input, and is
    /// offered to the next connection.
    /// </summary>
    public InputMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_mode == value)
                {
                    return;
                }

                ReleaseLocked();
                _mode = value;
            }

            _log?.Invoke($"input: {value}");
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether a new stream should offer input to the client (<c>stream_config.input</c>).</summary>
    public bool Offered => Mode != InputMode.Off;

    public HostInputCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new(_framesHandled, _framesIgnored, _injections, _injectionFailures, _releases);
            }
        }
    }

    /// <summary>A stream has started on <paramref name="owner"/>: input from it maps onto a frame of this size.</summary>
    public void BeginStream(object owner, int frameWidth, int frameHeight)
    {
        lock (_gate)
        {
            ReleaseLocked();
            _owner = owner;
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _lastSequence = null;
        }
    }

    /// <summary>The stream on <paramref name="owner"/> is over. Releases everything if it was the current one.</summary>
    public void EndStream(object owner)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_owner, owner))
            {
                ReleaseLocked();
                _owner = null;
            }
        }
    }

    /// <summary>One frame from <paramref name="owner"/>. Ignored unless it is the current stream, input is on, and it is newer than the last.</summary>
    public void Handle(object owner, TouchFrame frame)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner) || _mode == InputMode.Off || (_lastSequence is { } last && frame.Sequence <= last))
            {
                _framesIgnored++;
                return;
            }

            _lastSequence = frame.Sequence;
            _framesHandled++;

            // The monitor is looked up when a gesture begins, so that a moved or re-arranged display is followed.
            bool beginning = _active.Count == 0 && _ignored.Count == 0 && _gestures.IsIdle;
            if (beginning || _monitor is null)
            {
                _monitor = _monitors.Locate(_monitorName());
                if (_monitor is null)
                {
                    LogOnce("input: the mirrored monitor was not found; touches are ignored");
                    ReleaseLocked();
                    return;
                }
            }

            if (_mode == InputMode.Touch)
            {
                HandleTouchLocked(frame);
            }
            else
            {
                _gestures.Handle(frame, (contact, clamp) => Map(contact, clamp));
            }
        }
    }

    /// <summary>The timer's work: refresh held touch contacts; decide a press-and-hold in mouse mode.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_mode == InputMode.Touch && _active.Count > 0 && _nowMillis() - _lastInjectionMillis >= RefreshInterval.TotalMilliseconds)
            {
                InjectLocked([.. _active.Select(pair => new InjectedContact(pair.Key, pair.Value, ContactState.Update))]);
            }
            else if (_mode == InputMode.Mouse)
            {
                _gestures.Tick();
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            ReleaseLocked();
            _owner = null;
            _touch?.Dispose();
            _touch = null;
        }
    }

    // ---- Touch mode ----------------------------------------------------------------------------

    private void HandleTouchLocked(TouchFrame frame)
    {
        // Where every contact on the desktop is after this frame, and which of them go down, lift or cancel in it.
        var positions = new Dictionary<byte, ScreenPoint>(_active);
        var downs = new HashSet<byte>();
        var lifts = new HashSet<byte>();
        var cancels = new HashSet<byte>();
        bool liftMoved = false;
        foreach (TouchContact contact in frame.Contacts)
        {
            bool known = positions.ContainsKey(contact.Id);
            switch (contact.Phase)
            {
                case TouchPhase.Down or TouchPhase.Move when !known:
                    // A down, or a move for a contact never seen down: it starts here, if on the picture.
                    if (_ignored.Contains(contact.Id))
                    {
                        break;
                    }

                    if (Map(contact, clamp: false) is { } start)
                    {
                        positions[contact.Id] = start;
                        downs.Add(contact.Id);
                    }
                    else
                    {
                        _ignored.Add(contact.Id);
                    }

                    break;
                case TouchPhase.Down or TouchPhase.Move:
                    positions[contact.Id] = Map(contact, clamp: true)!.Value;
                    break;
                case TouchPhase.Up when known:
                    ScreenPoint lifted = Map(contact, clamp: true)!.Value;
                    liftMoved |= lifted != positions[contact.Id];
                    positions[contact.Id] = lifted;
                    lifts.Add(contact.Id);
                    break;
                case TouchPhase.Cancel when known:
                    cancels.Add(contact.Id);
                    break;
                default:
                    // An up or cancel of a contact that was ignored or never seen.
                    _ignored.Remove(contact.Id);
                    break;
            }
        }

        // Windows requires an up at the last updated position: a contact that lifted somewhere else
        // is first moved there, in a frame of its own.
        if (liftMoved)
        {
            if (!InjectLocked([.. positions.Select(pair => new InjectedContact(pair.Key, pair.Value, downs.Contains(pair.Key) ? ContactState.Down : ContactState.Update))]))
            {
                return;
            }

            downs.Clear();
        }

        // Every frame lists every contact on the desktop; those not mentioned are held where they are.
        List<InjectedContact> injected = [.. positions.Select(pair => new InjectedContact(
            pair.Key,
            pair.Value,
            lifts.Contains(pair.Key) ? ContactState.Up
                : cancels.Contains(pair.Key) ? ContactState.Cancel
                : downs.Contains(pair.Key) ? ContactState.Down
                : ContactState.Update))];
        if (injected.Count > 0 && !InjectLocked(injected))
        {
            return;
        }

        _active.Clear();
        foreach ((byte id, ScreenPoint point) in positions.Where(pair => !lifts.Contains(pair.Key) && !cancels.Contains(pair.Key)))
        {
            _active[id] = point;
        }
    }

    private bool InjectLocked(IReadOnlyList<InjectedContact> frame)
    {
        try
        {
            _touch ??= _touchFactory();
            if (_touch.Inject(frame))
            {
                _injections++;
                _lastInjectionMillis = _nowMillis();
                return true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogOnce($"input: synthetic touch is not available ({exception.Message}); choose Mouse input instead");
        }

        // A refused frame: Windows cancels its contacts itself; this side forgets them.
        _injectionFailures++;
        LogOnce("input: the system refused a touch frame; active contacts were dropped");
        _active.Clear();
        _ignored.Clear();
        return false;
    }

    // ---- Shared ----------------------------------------------------------------------------------

    private ScreenPoint? Map(TouchContact contact, bool clamp) =>
        _monitor is { } monitor ? InputGeometry.Map(contact.X, contact.Y, _frameWidth, _frameHeight, monitor, clamp) : null;

    private void ReleaseLocked()
    {
        bool anything = _active.Count > 0 || !_gestures.IsIdle;
        if (_active.Count > 0 && _touch is not null)
        {
            InjectLocked([.. _active.Select(pair => new InjectedContact(pair.Key, pair.Value, ContactState.Cancel))]);
        }

        _active.Clear();
        _ignored.Clear();
        _gestures.Release();
        _monitor = null;
        if (anything)
        {
            _releases++;
        }
    }

    private void LogOnce(string line)
    {
        if (!_failureLogged)
        {
            _failureLogged = true;
            _log?.Invoke(line);
        }
    }
}

/// <summary>
/// Mouse mode (input architecture section 5): tap, drag, press-and-hold right click, two-finger
/// wheel. Pure: time comes from the caller and output goes to an <see cref="IMouseInjector"/>.
/// </summary>
internal sealed class MouseGestures(IMouseInjector mouse, Func<long> nowMillis)
{
    private enum Stage
    {
        Idle,
        Pending,
        Dragging,
        Scrolling,
        Swallowing,
    }

    private Stage _stage = Stage.Idle;
    private byte _primary;
    private ScreenPoint _downPoint;
    private long _downMillis;
    private readonly Dictionary<byte, ScreenPoint> _fingers = [];
    private (double X, double Y) _centroid;
    private double _wheelY;
    private double _wheelX;

    public bool IsIdle => _stage == Stage.Idle && _fingers.Count == 0;

    public void Handle(TouchFrame frame, Func<TouchContact, bool, ScreenPoint?> map)
    {
        foreach (TouchContact contact in frame.Contacts)
        {
            bool down = contact.Phase is TouchPhase.Down or TouchPhase.Move;
            if (down && !_fingers.ContainsKey(contact.Id))
            {
                if (map(contact, false) is not { } start)
                {
                    continue;
                }

                _fingers[contact.Id] = start;
                Began(contact.Id, start);
            }
            else if (down)
            {
                ScreenPoint point = map(contact, true)!.Value;
                _fingers[contact.Id] = point;
                Moved(contact.Id, point);
            }
            else if (_fingers.Remove(contact.Id, out ScreenPoint last))
            {
                ScreenPoint end = contact.Phase == TouchPhase.Up ? map(contact, true) ?? last : last;
                Ended(contact.Id, end, cancelled: contact.Phase == TouchPhase.Cancel);
            }
        }

        // The wheel follows the fingers' centre once per frame, after every finger in it has moved.
        if (_stage == Stage.Scrolling && _fingers.Count > 0)
        {
            Scroll();
        }
    }

    public void Tick()
    {
        if (_stage == Stage.Pending && nowMillis() - _downMillis >= HostInput.LongPressTime.TotalMilliseconds)
        {
            mouse.Move(_downPoint);
            mouse.Button(MouseButton.Right, down: true);
            mouse.Button(MouseButton.Right, down: false);
            _stage = Stage.Swallowing;
        }
    }

    public void Release()
    {
        if (_stage == Stage.Dragging)
        {
            mouse.Button(MouseButton.Left, down: false);
        }

        _stage = Stage.Idle;
        _fingers.Clear();
    }

    private void Began(byte id, ScreenPoint point)
    {
        switch (_stage)
        {
            case Stage.Idle:
                _stage = Stage.Pending;
                _primary = id;
                _downPoint = point;
                _downMillis = nowMillis();
                break;
            case Stage.Pending when _fingers.Count == 2:
                _stage = Stage.Scrolling;
                _centroid = Centroid();
                _wheelX = _wheelY = 0;
                break;
            case Stage.Scrolling:
                // A finger joining moves the centre without anyone scrolling.
                _centroid = Centroid();
                break;
        }
    }

    private void Moved(byte id, ScreenPoint point)
    {
        switch (_stage)
        {
            case Stage.Pending when id == _primary && Distance(point, _downPoint) >= HostInput.TapSlopPixels:
                mouse.Move(_downPoint);
                mouse.Button(MouseButton.Left, down: true);
                mouse.Move(point);
                _stage = Stage.Dragging;
                break;
            case Stage.Dragging when id == _primary:
                mouse.Move(point);
                break;
        }
    }

    private void Scroll()
    {
        (double x, double y) = Centroid();
        _wheelY += y - _centroid.Y;
        _wheelX -= x - _centroid.X;
        _centroid = (x, y);
        int notchesY = (int)(_wheelY / HostInput.ScrollPixelsPerNotch);
        int notchesX = (int)(_wheelX / HostInput.ScrollPixelsPerNotch);
        if (notchesY != 0 || notchesX != 0)
        {
            mouse.Wheel(notchesY * 120, notchesX * 120);
            _wheelY -= notchesY * HostInput.ScrollPixelsPerNotch;
            _wheelX -= notchesX * HostInput.ScrollPixelsPerNotch;
        }
    }

    private void Ended(byte id, ScreenPoint point, bool cancelled)
    {
        switch (_stage)
        {
            case Stage.Pending when id == _primary && !cancelled:
                // ES-135: every still press is a click or a right click. Before, a press lifted
                // between TapTime (500 ms) and the long press (600 ms) did nothing; and as times are
                // the PC's arrival times, Wi-Fi jitter could stretch a short tap into that gap. A
                // press that reached the long press before the 50 ms tick noticed it is a right click.
                bool held = nowMillis() - _downMillis >= HostInput.LongPressTime.TotalMilliseconds;
                MouseButton button = held ? MouseButton.Right : MouseButton.Left;
                mouse.Move(_downPoint);
                mouse.Button(button, down: true);
                mouse.Button(button, down: false);
                break;
            case Stage.Dragging when id == _primary:
                if (!cancelled)
                {
                    mouse.Move(point);
                }

                mouse.Button(MouseButton.Left, down: false);
                break;
        }

        if (_fingers.Count == 0)
        {
            _stage = Stage.Idle;
        }
        else if (_stage is Stage.Pending or Stage.Dragging && id == _primary)
        {
            // The primary finger lifted while others stay down: nothing more until all lift.
            _stage = Stage.Swallowing;
        }
    }

    private (double X, double Y) Centroid() => (_fingers.Values.Average(point => point.X), _fingers.Values.Average(point => point.Y));

    private static double Distance(ScreenPoint a, ScreenPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
