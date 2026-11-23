using System.Text.Json;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Tests.Sessions;

namespace ExtendSpace.Core.Tests.Input;

internal sealed class RecordingTouch : ITouchInjector
{
    public List<IReadOnlyList<InjectedContact>> Frames { get; } = [];

    public bool Refuse { get; set; }

    public bool Disposed { get; private set; }

    public bool Inject(IReadOnlyList<InjectedContact> frame)
    {
        Frames.Add([.. frame]);
        return !Refuse;
    }

    public void Dispose() => Disposed = true;

    public string Last => Describe(Frames[^1]);

    public static string Describe(IReadOnlyList<InjectedContact> frame) =>
        string.Join(" ", frame.OrderBy(contact => contact.PointerId).Select(contact => $"{contact.PointerId}:{contact.State}@{contact.Point.X},{contact.Point.Y}"));
}

internal sealed class RecordingMouse : IMouseInjector
{
    public List<string> Events { get; } = [];

    public void Move(ScreenPoint point) => Events.Add($"move {point.X},{point.Y}");

    public void Button(MouseButton button, bool down) => Events.Add($"{button} {(down ? "down" : "up")}");

    public void Wheel(int vertical, int horizontal) => Events.Add($"wheel {vertical},{horizontal}");
}

internal sealed class FixedMonitor(ScreenRect? rect) : IMonitorLocator
{
    public ScreenRect? Rect { get; set; } = rect;

    public string? AskedFor { get; private set; } = "never";

    public ScreenRect? Locate(string? deviceName)
    {
        AskedFor = deviceName;
        return Rect;
    }
}

public sealed class TouchFrameTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (JsonElement element in Load().GetProperty("cases").EnumerateArray())
        {
            data.Add(element.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frames_match_the_shared_vectors_both_ways(string name)
    {
        JsonElement vector = Load().GetProperty("cases").EnumerateArray().Single(element => element.GetProperty("name").GetString() == name);
        byte[] payload = Convert.FromHexString(vector.GetProperty("payload").GetString()!);
        var expected = new TouchFrame(
            vector.GetProperty("sequence").GetUInt32(),
            [.. vector.GetProperty("contacts").EnumerateArray().Select(c => new TouchContact(c.GetProperty("id").GetByte(), (TouchPhase)c.GetProperty("phase").GetByte(), c.GetProperty("x").GetUInt16(), c.GetProperty("y").GetUInt16()))]);

        TouchFrame parsed = TouchFrame.Parse(payload);
        Assert.Equal(expected.Sequence, parsed.Sequence);
        Assert.Equal(expected.Contacts, parsed.Contacts);
        Assert.Equal(payload, expected.Encode());
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("000000000000000000")]
    [InlineData("00000000" + "0b000000")]
    [InlineData("0000000001000000" + "0000000000000000" + "00")]
    [InlineData("0000000002000000" + "0100000000000000" + "0101000000000000")]
    [InlineData("0000000001000000" + "2000000000000000")]
    [InlineData("0000000001000000" + "0004000000000000")]
    public void Malformed_frames_are_a_protocol_violation(string hex)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(() => TouchFrame.Parse(Convert.FromHexString(hex)));
        Assert.Equal(WireErrorCode.MalformedMessage, error.Code);
    }

    private static JsonElement Load() => JsonDocument.Parse(File.ReadAllText(SharedTestData.Find("tests/input-vectors/touch-v1.json"))).RootElement;
}

public sealed class InputGeometryTests
{
    // (monitor, stream, picture point) → screen point, by hand: the cases a user meets.
    [Theory]
    // Same shape, no bars: centre and corners of a 1920 × 1080 main monitor.
    [InlineData(0, 0, 1920, 1080, 1920, 1080, 32768, 32768, 960, 540)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080, 0, 0, 0, 0)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080, 65535, 65535, 1919, 1079)]
    // A 2560 × 1440 monitor streamed at 1920 × 1080: scaled, same shape.
    [InlineData(0, 0, 2560, 1440, 1920, 1080, 16384, 49151, 640, 1080)]
    // A second monitor to the left of the main one (negative origin).
    [InlineData(-1920, 0, 1920, 1080, 1920, 1080, 0, 32768, -1920, 540)]
    // A monitor above, offset to the right.
    [InlineData(300, -1200, 1920, 1200, 1920, 1080, 32768, 0, 1260, -1200)]
    // A 16:10 monitor in a 16:9 stream: pillarboxed; the picture's left edge is the monitor's left edge.
    // The bar is 96 px wide: x = 96 / 1920 of the frame (3277) is the picture's, and the monitor's, left edge.
    [InlineData(0, 0, 2560, 1600, 1920, 1080, 3277, 32768, 0, 800)]
    public void Picture_points_map_to_the_monitor(int left, int top, int width, int height, int streamW, int streamH, ushort x, ushort y, int expectedX, int expectedY)
    {
        ScreenPoint? point = InputGeometry.Map(x, y, streamW, streamH, new ScreenRect(left, top, width, height), clamp: false);
        Assert.NotNull(point);
        Assert.InRange(point.Value.X, expectedX - 1, expectedX + 1);
        Assert.InRange(point.Value.Y, expectedY - 1, expectedY + 1);
    }

    [Fact]
    public void A_point_in_the_encoders_bars_is_refused_at_touch_down_and_clamped_while_moving()
    {
        var monitor = new ScreenRect(0, 0, 2560, 1600);

        // 2560 × 1600 into 1920 × 1080: the picture is 1728 wide at x = 96; x = 0..95 is a bar.
        Assert.Equal((96, 0, 1728, 1080), InputGeometry.Picture(2560, 1600, 1920, 1080));
        Assert.Null(InputGeometry.Map(1000, 32768, 1920, 1080, monitor, clamp: false));
        Assert.Equal(new ScreenPoint(0, 800), InputGeometry.Map(1000, 32768, 1920, 1080, monitor, clamp: true));
        Assert.Equal(new ScreenPoint(2559, 800), InputGeometry.Map(65535, 32768, 1920, 1080, monitor, clamp: true));
    }

    [Fact]
    public void A_portrait_monitor_in_a_landscape_stream_is_centred_with_bars_at_the_sides()
    {
        (int x, int y, int w, int h) = InputGeometry.Picture(1080, 1920, 1920, 1080);
        Assert.Equal((656, 0, 608, 1080), (x, y, w, h));
        Assert.Equal(new ScreenPoint(540, 960), InputGeometry.Map(32768, 32768, 1920, 1080, new ScreenRect(0, 0, 1080, 1920), clamp: false));
    }

    [Fact]
    public void Nothing_maps_onto_an_empty_monitor_or_stream()
    {
        Assert.Null(InputGeometry.Map(1, 1, 0, 1080, new ScreenRect(0, 0, 1920, 1080), clamp: true));
        Assert.Null(InputGeometry.Map(1, 1, 1920, 1080, new ScreenRect(0, 0, 0, 0), clamp: true));
    }
}

public sealed class HostInputTouchTests
{
    private static readonly object Stream = new();
    private readonly RecordingTouch _touch = new();
    private readonly RecordingMouse _mouse = new();
    private readonly FixedMonitor _monitor = new(new ScreenRect(0, 0, 1920, 1080));
    private long _now = 1_000;
    private readonly HostInput _input;

    public HostInputTouchTests()
    {
        _input = new HostInput(_monitor, () => _touch, _mouse, () => @"\\.\DISPLAY2", nowMillis: () => _now, autoTick: false) { Mode = InputMode.Touch };
        _input.BeginStream(Stream, 1920, 1080);
    }

    private uint _sequence;

    private void Send(params TouchContact[] contacts) => _input.Handle(Stream, new TouchFrame(_sequence++, contacts));

    private static TouchContact C(byte id, TouchPhase phase, ushort x = 32768, ushort y = 32768) => new(id, phase, x, y);

    [Fact]
    public void A_tap_is_a_down_then_an_up_at_the_same_place_on_the_named_monitor()
    {
        Send(C(0, TouchPhase.Down));
        Send(C(0, TouchPhase.Up));

        Assert.Equal(["0:Down@960,540", "0:Up@960,540"], _touch.Frames.Select(RecordingTouch.Describe));
        Assert.Equal(@"\\.\DISPLAY2", _monitor.AskedFor);
    }

    [Fact]
    public void An_up_somewhere_else_is_preceded_by_a_move_there()
    {
        Send(C(0, TouchPhase.Down, 0, 0));
        Send(C(0, TouchPhase.Up, 65535, 65535));

        Assert.Equal(["0:Down@0,0", "0:Update@1919,1079", "0:Up@1919,1079"], _touch.Frames.Select(RecordingTouch.Describe));
    }

    [Fact]
    public void Every_frame_lists_every_contact_on_the_desktop()
    {
        Send(C(0, TouchPhase.Down, 0, 0));
        Send(C(1, TouchPhase.Down, 65535, 0));
        Send(C(0, TouchPhase.Move, 100, 100), C(1, TouchPhase.Up, 65535, 0));
        Send(C(0, TouchPhase.Move, 200, 200));

        Assert.Equal(
            ["0:Down@0,0", "0:Update@0,0 1:Down@1919,0", "0:Update@3,2 1:Up@1919,0", "0:Update@6,3"],
            _touch.Frames.Select(RecordingTouch.Describe));
    }

    [Fact]
    public void A_held_contact_is_refreshed_every_100_ms_and_not_sooner()
    {
        Send(C(0, TouchPhase.Down));
        _now += 99;
        _input.Tick();
        Assert.Single(_touch.Frames);

        _now += 1;
        _input.Tick();
        Assert.Equal("0:Update@960,540", _touch.Last);
    }

    [Fact]
    public void A_touch_that_starts_in_the_encoders_bars_is_ignored_for_its_whole_life()
    {
        _monitor.Rect = new ScreenRect(0, 0, 2560, 1600);
        _input.BeginStream(Stream, 1920, 1080);

        Send(C(0, TouchPhase.Down, 1000, 32768));
        Send(C(0, TouchPhase.Move, 32768, 32768));
        Send(C(0, TouchPhase.Up, 32768, 32768));

        Assert.Empty(_touch.Frames);
    }

    [Fact]
    public void A_move_into_the_bars_is_clamped_to_the_edge_of_the_picture()
    {
        _monitor.Rect = new ScreenRect(0, 0, 2560, 1600);
        _input.BeginStream(Stream, 1920, 1080);

        Send(C(0, TouchPhase.Down, 32768, 32768));
        Send(C(0, TouchPhase.Move, 0, 32768));

        Assert.Equal("0:Update@0,800", _touch.Last);
    }

    [Theory]
    [InlineData("end")]
    [InlineData("new stream")]
    [InlineData("off")]
    [InlineData("dispose")]
    public void Every_end_cancels_every_held_contact_and_later_frames_inject_nothing(string how)
    {
        Send(C(0, TouchPhase.Down, 0, 0), C(1, TouchPhase.Down, 65535, 65535));

        switch (how)
        {
            case "end":
                _input.EndStream(Stream);
                break;
            case "new stream":
                _input.BeginStream(new object(), 1920, 1080);
                break;
            case "off":
                _input.Mode = InputMode.Off;
                break;
            case "dispose":
                _input.Dispose();
                break;
        }

        Assert.Equal("0:Cancel@0,0 1:Cancel@1919,1079", _touch.Last);
        int injected = _touch.Frames.Count;
        Send(C(0, TouchPhase.Move));
        _now += 500;
        _input.Tick();
        Assert.Equal(injected, _touch.Frames.Count);
        Assert.Equal(1, _input.Counters.Releases);
    }

    [Fact]
    public void Frames_from_another_connection_or_older_than_the_last_are_ignored()
    {
        _input.Handle(new object(), new TouchFrame(0, [C(0, TouchPhase.Down)]));
        Send(C(0, TouchPhase.Down));
        _input.Handle(Stream, new TouchFrame(0, [C(0, TouchPhase.Up)]));

        Assert.Single(_touch.Frames);
        Assert.Equal(2, _input.Counters.FramesIgnored);
    }

    [Fact]
    public void Without_the_mirrored_monitor_nothing_is_injected()
    {
        _monitor.Rect = null;
        Send(C(0, TouchPhase.Down));
        Assert.Empty(_touch.Frames);
    }

    [Fact]
    public void A_frame_Windows_refuses_drops_the_contacts_so_nothing_is_left_held()
    {
        Send(C(0, TouchPhase.Down));
        _touch.Refuse = true;
        Send(C(0, TouchPhase.Move, 0, 0));
        _touch.Refuse = false;
        _now += 500;
        _input.Tick();

        Assert.Equal(2, _touch.Frames.Count);
        Assert.Equal(1, _input.Counters.InjectionFailures);
    }

    [Fact]
    public void A_cancel_lifts_the_contact_where_it_last_was()
    {
        Send(C(0, TouchPhase.Down, 0, 0));
        Send(C(0, TouchPhase.Cancel, 65535, 65535));
        Assert.Equal("0:Cancel@0,0", _touch.Last);
    }
}

public sealed class HostInputMouseTests
{
    private static readonly object Stream = new();
    private readonly RecordingMouse _mouse = new();
    private long _now = 1_000;
    private uint _sequence;
    private readonly HostInput _input;

    public HostInputMouseTests()
    {
        _input = new HostInput(new FixedMonitor(new ScreenRect(0, 0, 1920, 1080)), () => new RecordingTouch(), _mouse, () => null, nowMillis: () => _now, autoTick: false) { Mode = InputMode.Mouse };
        _input.BeginStream(Stream, 1920, 1080);
    }

    private void Send(params TouchContact[] contacts) => _input.Handle(Stream, new TouchFrame(_sequence++, contacts));

    private static ushort Px(int pixels, int span) => (ushort)Math.Round(pixels * 65535.0 / (span - 1));

    private static TouchContact C(byte id, TouchPhase phase, int x, int y) => new(id, phase, Px(x, 1920), Px(y, 1080));

    [Fact]
    public void A_tap_is_a_left_click_where_the_finger_went_down()
    {
        Send(C(0, TouchPhase.Down, 100, 100));
        Send(C(0, TouchPhase.Move, 105, 103));
        _now += 120;
        Send(C(0, TouchPhase.Up, 105, 103));

        Assert.Equal(["move 100,100", "Left down", "Left up"], _mouse.Events);
    }

    [Fact]
    public void A_drag_presses_at_the_start_follows_the_finger_and_releases_at_the_end()
    {
        Send(C(0, TouchPhase.Down, 100, 100));
        Send(C(0, TouchPhase.Move, 140, 100));
        Send(C(0, TouchPhase.Move, 300, 200));
        Send(C(0, TouchPhase.Up, 310, 210));

        Assert.Equal(["move 100,100", "Left down", "move 140,100", "move 300,200", "move 310,210", "Left up"], _mouse.Events);
    }

    [Fact]
    public void Holding_still_for_600_ms_is_a_right_click_and_the_rest_of_the_touch_is_swallowed()
    {
        Send(C(0, TouchPhase.Down, 500, 500));
        _now += 599;
        _input.Tick();
        Assert.Empty(_mouse.Events);

        _now += 1;
        _input.Tick();
        Send(C(0, TouchPhase.Move, 700, 700));
        Send(C(0, TouchPhase.Up, 700, 700));

        Assert.Equal(["move 500,500", "Right down", "Right up"], _mouse.Events);
    }

    [Fact]
    public void Two_fingers_moving_scroll_the_wheel_in_the_direction_the_content_follows()
    {
        Send(C(0, TouchPhase.Down, 500, 500));
        Send(C(0, TouchPhase.Move, 500, 500), C(1, TouchPhase.Down, 600, 500));
        Send(C(0, TouchPhase.Move, 500, 600), C(1, TouchPhase.Move, 600, 600));
        Send(C(0, TouchPhase.Move, 400, 600), C(1, TouchPhase.Move, 500, 600));
        Send(C(0, TouchPhase.Up, 400, 600), C(1, TouchPhase.Up, 500, 600));

        // Down 100 px: two notches towards the top; left 100 px: two notches towards the right.
        Assert.Equal(["wheel 240,0", "wheel 0,240"], _mouse.Events);
    }

    [Fact]
    public void Ending_during_a_drag_releases_the_button()
    {
        Send(C(0, TouchPhase.Down, 100, 100));
        Send(C(0, TouchPhase.Move, 200, 100));

        _input.EndStream(Stream);

        Assert.Equal("Left up", _mouse.Events[^1]);
        Send(C(0, TouchPhase.Up, 200, 100));
        Assert.Equal("Left up", _mouse.Events[^1]);
    }

    [Theory]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(550)]
    [InlineData(599)]
    public void A_still_press_lifted_before_the_long_press_is_always_a_click(int heldMillis)
    {
        // ES-135: 500-600 ms used to do nothing at all.
        Send(C(0, TouchPhase.Down, 300, 300));
        _now += heldMillis;
        _input.Tick();
        Send(C(0, TouchPhase.Up, 300, 300));

        Assert.Equal(["move 300,300", "Left down", "Left up"], _mouse.Events);
    }

    [Fact]
    public void A_still_press_lifted_after_600_ms_before_the_tick_saw_it_is_a_right_click()
    {
        Send(C(0, TouchPhase.Down, 300, 300));
        _now += 630;
        Send(C(0, TouchPhase.Up, 300, 300));
        _input.Tick();

        Assert.Equal(["move 300,300", "Right down", "Right up"], _mouse.Events);
    }

    [Fact]
    public void A_cancelled_touch_never_clicks()
    {
        Send(C(0, TouchPhase.Down, 100, 100));
        Send(C(0, TouchPhase.Cancel, 100, 100));
        Assert.Empty(_mouse.Events);
    }
}

public sealed class InputOverConnectionTests : IDisposable
{
    private readonly LoopbackTransport _transport = new();
    private readonly RecordingTouch _touch = new();
    private readonly HostInput _input;
    private readonly HostConnectionController _controller;

    public InputOverConnectionTests()
    {
        _input = new HostInput(new FixedMonitor(new ScreenRect(0, 0, 1920, 1080)), () => _touch, new RecordingMouse(), () => null, autoTick: false);
        _controller = new HostConnectionController(_transport, new HostConnectionOptions { Input = _input });
    }

    public void Dispose()
    {
        _controller.CloseAsync("test over", null, CancellationToken.None).Wait(Loopback.Patience);
        _transport.Dispose();
        _input.Dispose();
    }

    private async Task<(TestClient Client, StreamConfigMessage Config)> StreamAsync()
    {
        Task<AttemptOutcome> open = _controller.OpenAsync(TestContext.Current.CancellationToken);
        TestClient client = await _transport.AcceptAsync();
        await open.Within();
        Task<AttemptOutcome<ClientCapabilities>> exchange = _controller.ExchangeCapabilitiesAsync(TestContext.Current.CancellationToken);
        await client.SendHelloAsync();
        await client.ExpectAsync<HelloMessage>();
        await client.SendAsync(Loopback.Tablet);
        await exchange.Within();
        Task<AttemptOutcome<PeerDescription>> configure = _controller.ConfigureStreamAsync(Loopback.Stream, TestContext.Current.CancellationToken);
        StreamConfigMessage config = await client.ExpectAsync<StreamConfigMessage>();
        await client.SendAsync(new StreamReadyMessage("c2.fake.avc.decoder", true));
        Assert.Equal(OutcomeKind.Succeeded, (await configure.Within()).Kind);
        return (client, config);
    }

    private static byte[] TouchFrameBytes(TouchFrame frame)
    {
        byte[] payload = frame.Encode();
        var bytes = new byte[WireFormat.HeaderLength + payload.Length];
        WireFormat.WriteHeader(bytes, new FrameHeader(WireFormat.ChannelInput, TouchFrame.MessageType, 0, (uint)payload.Length));
        payload.CopyTo(bytes, WireFormat.HeaderLength);
        return bytes;
    }

    private async Task WaitForFramesAsync(int count)
    {
        long deadline = Environment.TickCount64 + 5_000;
        while (_touch.Frames.Count < count)
        {
            Assert.True(Environment.TickCount64 < deadline, "the touch was not injected in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task With_input_off_nothing_is_offered_and_a_touch_frame_ends_the_connection()
    {
        (TestClient client, StreamConfigMessage config) = await StreamAsync();
        using (client)
        {
            Assert.False(config.InputTouch);
            await client.SendAsync(TouchFrameBytes(new TouchFrame(0, [new TouchContact(0, TouchPhase.Down, 1, 1)])));

            List<object> seen = await client.ReadUntilClosedAsync().Within();
            Assert.Contains(seen, message => message is ErrorMessage { Code: WireErrorCode.UnexpectedMessage, Fatal: true });
            Assert.Empty(_touch.Frames);
        }
    }

    [Fact]
    public async Task With_touch_on_the_stream_offers_input_touches_are_injected_and_the_end_of_the_connection_releases_them()
    {
        _input.Mode = InputMode.Touch;
        (TestClient client, StreamConfigMessage config) = await StreamAsync();
        using (client)
        {
            Assert.True(config.InputTouch);
            await client.SendAsync(TouchFrameBytes(new TouchFrame(0, [new TouchContact(0, TouchPhase.Down, 32768, 32768)])));
            await WaitForFramesAsync(1);
            Assert.Equal("0:Down@960,540", _touch.Last);

            client.CloseAbruptly();
            Assert.Equal(ConnectionEndKind.Lost, (await _controller.WaitForEndAsync(TestContext.Current.CancellationToken).Within()).Kind);
            await WaitForFramesAsync(2);
            Assert.Equal("0:Cancel@960,540", _touch.Last);
        }
    }

    [Fact]
    public async Task A_malformed_touch_frame_is_a_fatal_protocol_error()
    {
        _input.Mode = InputMode.Touch;
        (TestClient client, _) = await StreamAsync();
        using (client)
        {
            byte[] bytes = TouchFrameBytes(new TouchFrame(0, [new TouchContact(0, TouchPhase.Down, 1, 1)]));
            bytes[WireFormat.HeaderLength + 4] = 11;
            await client.SendAsync(bytes);

            List<object> seen = await client.ReadUntilClosedAsync().Within();
            Assert.Contains(seen, message => message is ErrorMessage { Code: WireErrorCode.MalformedMessage });
        }
    }

    [Fact]
    public async Task Input_types_from_a_later_version_are_ignored_on_a_stream_that_offered_input()
    {
        _input.Mode = InputMode.Touch;
        (TestClient client, _) = await StreamAsync();
        using (client)
        {
            byte[] bytes = new byte[WireFormat.HeaderLength + 3];
            WireFormat.WriteHeader(bytes, new FrameHeader(WireFormat.ChannelInput, 99, 0, 3));
            await client.SendAsync(bytes);
            await client.SendAsync(TouchFrameBytes(new TouchFrame(0, [new TouchContact(0, TouchPhase.Down, 0, 0)])));

            await WaitForFramesAsync(1);
            Assert.Equal("0:Down@0,0", _touch.Last);
        }
    }
}
