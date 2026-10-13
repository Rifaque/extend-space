using System.Buffers;
using System.Text.Json;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>First message in each direction: which protocol versions the sender speaks, and who it is.</summary>
internal sealed record Hello(int ProtocolMin, int ProtocolMax, string Role, string DeviceName, string AppVersion);

internal sealed record DecoderCapability(string Name, bool Hardware, bool LowLatency, int MaxWidth, int MaxHeight);

/// <summary>What the client can show and decode. The host chooses within it.</summary>
internal sealed record ClientCapabilities(
    int DisplayWidth,
    int DisplayHeight,
    double RefreshHz,
    IReadOnlyList<DecoderCapability> Decoders);

/// <summary>What the host is about to send.</summary>
internal sealed record StreamConfig(
    string Codec,
    string Profile,
    int Width,
    int Height,
    int FrameRate,
    int Bitrate,
    int MaxVideoPayload,
    string? Encoder,
    string? EncoderAcceleration);

internal sealed record StreamReady(string? Decoder, bool? Hardware);

internal sealed record KeyframeRequest(string Reason);

/// <param name="HostTimeMicros">The host's monotonic clock when the ping was written.</param>
/// <param name="ClockOffsetMicros">
/// The host's current estimate of "client clock minus host clock", so that the client can express
/// its own event times on the host's clock. Absent until a pong has been received.
/// </param>
/// <param name="RoundTripMicros">The round trip the estimate was taken from; it is good to half of this.</param>
internal sealed record Ping(long Id, long HostTimeMicros, long? ClockOffsetMicros, long? RoundTripMicros);

/// <param name="ClientTimeMicros">The client's monotonic clock when it answered.</param>
internal sealed record Pong(long Id, long HostTimeMicros, long ClientTimeMicros);

/// <summary>Delays over one reporting interval, in milliseconds, from capture on the host to an event on the client.</summary>
internal sealed record LatencySummary(int Count, double MinMs, double MeanMs, double P95Ms, double MaxMs);

/// <summary>The client's counters, cumulative since the connection was accepted, and its latest latency figures.</summary>
internal sealed record ClientStats
{
    public long FramesReceived { get; init; }

    public long BytesReceived { get; init; }

    /// <summary>Frames the sequence numbers show were never received.</summary>
    public long FramesMissing { get; init; }

    /// <summary>Received but not given to the decoder: no surface, waiting for a keyframe, or backlog.</summary>
    public long FramesDiscarded { get; init; }

    public long FramesSubmitted { get; init; }

    public long FramesDecoded { get; init; }

    /// <summary>Decoded frames released to the surface.</summary>
    public long FramesRendered { get; init; }

    /// <summary>Frames the system reported as shown. Android documents this as informational.</summary>
    public long FramesDisplayed { get; init; }

    public long DecoderErrors { get; init; }

    public int QueueDepth { get; init; }

    public LatencySummary? ReceiveLatency { get; init; }

    public LatencySummary? RenderLatency { get; init; }

    public LatencySummary? DisplayLatency { get; init; }

    /// <summary>Adds another report's counters to these. Latency figures and queue depth do not add up and are dropped.</summary>
    public ClientStats Plus(ClientStats other) => new()
    {
        FramesReceived = FramesReceived + other.FramesReceived,
        BytesReceived = BytesReceived + other.BytesReceived,
        FramesMissing = FramesMissing + other.FramesMissing,
        FramesDiscarded = FramesDiscarded + other.FramesDiscarded,
        FramesSubmitted = FramesSubmitted + other.FramesSubmitted,
        FramesDecoded = FramesDecoded + other.FramesDecoded,
        FramesRendered = FramesRendered + other.FramesRendered,
        FramesDisplayed = FramesDisplayed + other.FramesDisplayed,
        DecoderErrors = DecoderErrors + other.DecoderErrors,
    };
}

/// <param name="ArrivedMicros">The host clock when the report was received.</param>
internal sealed record ClientStatsSample(ClientStats Stats, long ArrivedMicros);

/// <summary>
/// Combines the client's once-a-second latency summaries into figures for a whole run. The mean is
/// exact (weighted by frame count). Percentiles cannot be combined exactly from summaries, so the
/// 95th percentile is reported as the median and the worst of the per-second values.
/// </summary>
internal sealed class LatencyAggregate
{
    private readonly List<double> _p95 = [];
    private double _weightedSum;

    public long Frames { get; private set; }

    public int Reports => _p95.Count;

    public double? MeanMs => Frames > 0 ? _weightedSum / Frames : null;

    public double? MinMs { get; private set; }

    public double? MaxMs { get; private set; }

    public double? MedianP95Ms => _p95.Count > 0 ? _p95.Order().ElementAt((_p95.Count - 1) / 2) : null;

    public double? WorstP95Ms => _p95.Count > 0 ? _p95.Max() : null;

    public void Add(LatencySummary? summary)
    {
        if (summary is null || summary.Count <= 0)
        {
            return;
        }

        Frames += summary.Count;
        _weightedSum += summary.MeanMs * summary.Count;
        MinMs = MinMs is { } min ? Math.Min(min, summary.MinMs) : summary.MinMs;
        MaxMs = MaxMs is { } max ? Math.Max(max, summary.MaxMs) : summary.MaxMs;
        _p95.Add(summary.P95Ms);
    }

    public LatencyAggregate Copy()
    {
        var copy = new LatencyAggregate { Frames = Frames, MinMs = MinMs, MaxMs = MaxMs, _weightedSum = _weightedSum };
        copy._p95.AddRange(_p95);
        return copy;
    }
}

internal sealed record ErrorMessage(string Code, string Message, bool Fatal);

internal sealed record Bye(string Reason);

/// <summary>
/// Control messages are small UTF-8 JSON objects. Unknown fields are ignored; a missing required
/// field, wrong type or nesting deeper than eight levels is a <c>malformed_message</c> error.
/// </summary>
internal static class ControlCodec
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = Protocol.MaxJsonDepth };

    /// <summary>A complete control frame: header and JSON payload.</summary>
    public static byte[] Encode(object message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(Protocol.HeaderLength);
        buffer.Advance(Protocol.HeaderLength);

        ControlType type;
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            type = Write(json, message);
            json.WriteEndObject();
        }

        byte[] frame = buffer.WrittenSpan.ToArray();
        int length = frame.Length - Protocol.HeaderLength;
        if (length > Protocol.MaxControlPayload)
        {
            throw new InvalidOperationException($"A {type} message of {length} bytes exceeds the control channel limit.");
        }

        Protocol.WriteHeader(frame, new FrameHeader(Protocol.ChannelControl, (byte)type, 0, (uint)length));
        return frame;
    }

    /// <summary>The message a control frame carries, or null if its type is one this build does not know.</summary>
    /// <exception cref="ProtocolException">The payload is not the JSON the type requires.</exception>
    public static object? Decode(byte type, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined((ControlType)type))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.ToArray(), ParseOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ErrorCode.MalformedMessage, $"A {(ControlType)type} message is not a JSON object.");
            }

            return Read((ControlType)type, root);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(ErrorCode.MalformedMessage, $"A {(ControlType)type} message is not valid JSON: {exception.Message}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new ProtocolException(ErrorCode.MalformedMessage, $"A {(ControlType)type} message has a missing or mistyped field: {exception.Message}");
        }
    }

    private static ControlType Write(Utf8JsonWriter json, object message)
    {
        switch (message)
        {
            case Hello hello:
                json.WriteNumber("protocol_min", hello.ProtocolMin);
                json.WriteNumber("protocol_max", hello.ProtocolMax);
                json.WriteString("role", hello.Role);
                json.WriteString("device_name", hello.DeviceName);
                json.WriteString("app_version", hello.AppVersion);
                return ControlType.Hello;

            case ClientCapabilities capabilities:
                json.WriteStartObject("display");
                json.WriteNumber("width", capabilities.DisplayWidth);
                json.WriteNumber("height", capabilities.DisplayHeight);
                json.WriteNumber("refresh_hz", capabilities.RefreshHz);
                json.WriteEndObject();
                json.WriteStartArray("decoders");
                foreach (DecoderCapability decoder in capabilities.Decoders)
                {
                    json.WriteStartObject();
                    json.WriteString("codec", "h264");
                    json.WriteString("name", decoder.Name);
                    json.WriteBoolean("hardware", decoder.Hardware);
                    json.WriteBoolean("low_latency", decoder.LowLatency);
                    json.WriteNumber("max_width", decoder.MaxWidth);
                    json.WriteNumber("max_height", decoder.MaxHeight);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                return ControlType.Capabilities;

            case StreamConfig config:
                json.WriteString("codec", config.Codec);
                json.WriteString("profile", config.Profile);
                json.WriteNumber("width", config.Width);
                json.WriteNumber("height", config.Height);
                json.WriteNumber("frame_rate", config.FrameRate);
                json.WriteNumber("bitrate", config.Bitrate);
                json.WriteNumber("max_video_payload", config.MaxVideoPayload);
                if (config.Encoder is not null)
                {
                    json.WriteString("encoder", config.Encoder);
                }

                if (config.EncoderAcceleration is not null)
                {
                    json.WriteString("encoder_acceleration", config.EncoderAcceleration);
                }

                return ControlType.StreamConfig;

            case StreamReady ready:
                if (ready.Decoder is not null)
                {
                    json.WriteString("decoder", ready.Decoder);
                }

                if (ready.Hardware is { } hardware)
                {
                    json.WriteBoolean("hardware", hardware);
                }

                return ControlType.StreamReady;

            case KeyframeRequest request:
                json.WriteString("reason", request.Reason);
                return ControlType.RequestKeyframe;

            case Ping ping:
                json.WriteNumber("id", ping.Id);
                json.WriteNumber("host_time_us", ping.HostTimeMicros);
                if (ping.ClockOffsetMicros is { } offset)
                {
                    json.WriteNumber("clock_offset_us", offset);
                }

                if (ping.RoundTripMicros is { } roundTrip)
                {
                    json.WriteNumber("round_trip_us", roundTrip);
                }

                return ControlType.Ping;

            case Pong pong:
                json.WriteNumber("id", pong.Id);
                json.WriteNumber("host_time_us", pong.HostTimeMicros);
                json.WriteNumber("client_time_us", pong.ClientTimeMicros);
                return ControlType.Pong;

            case ClientStats stats:
                json.WriteNumber("frames_received", stats.FramesReceived);
                json.WriteNumber("bytes_received", stats.BytesReceived);
                json.WriteNumber("frames_missing", stats.FramesMissing);
                json.WriteNumber("frames_discarded", stats.FramesDiscarded);
                json.WriteNumber("frames_submitted", stats.FramesSubmitted);
                json.WriteNumber("frames_decoded", stats.FramesDecoded);
                json.WriteNumber("frames_rendered", stats.FramesRendered);
                json.WriteNumber("frames_displayed", stats.FramesDisplayed);
                json.WriteNumber("decoder_errors", stats.DecoderErrors);
                json.WriteNumber("queue_depth", stats.QueueDepth);
                WriteLatency(json, "receive_latency_ms", stats.ReceiveLatency);
                WriteLatency(json, "render_latency_ms", stats.RenderLatency);
                WriteLatency(json, "display_latency_ms", stats.DisplayLatency);
                return ControlType.Stats;

            case ErrorMessage error:
                json.WriteString("code", error.Code);
                json.WriteString("message", error.Message);
                json.WriteBoolean("fatal", error.Fatal);
                return ControlType.Error;

            case Bye bye:
                json.WriteString("reason", bye.Reason);
                return ControlType.Bye;

            default:
                throw new ArgumentException($"{message.GetType().Name} is not a control message.", nameof(message));
        }
    }

    private static object Read(ControlType type, JsonElement root) => type switch
    {
        ControlType.Hello => new Hello(
            root.GetProperty("protocol_min").GetInt32(),
            root.GetProperty("protocol_max").GetInt32(),
            OptionalString(root, "role") ?? string.Empty,
            OptionalString(root, "device_name") ?? string.Empty,
            OptionalString(root, "app_version") ?? string.Empty),

        ControlType.Capabilities => ReadCapabilities(root),

        ControlType.StreamConfig => new StreamConfig(
            RequiredString(root, "codec"),
            RequiredString(root, "profile"),
            root.GetProperty("width").GetInt32(),
            root.GetProperty("height").GetInt32(),
            root.GetProperty("frame_rate").GetInt32(),
            root.GetProperty("bitrate").GetInt32(),
            root.GetProperty("max_video_payload").GetInt32(),
            OptionalString(root, "encoder"),
            OptionalString(root, "encoder_acceleration")),

        ControlType.StreamReady => new StreamReady(
            OptionalString(root, "decoder"),
            root.TryGetProperty("hardware", out JsonElement hardware) ? hardware.GetBoolean() : null),

        ControlType.RequestKeyframe => new KeyframeRequest(OptionalString(root, "reason") ?? "unspecified"),

        ControlType.Ping => new Ping(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("host_time_us").GetInt64(),
            OptionalInt64(root, "clock_offset_us"),
            OptionalInt64(root, "round_trip_us")),

        ControlType.Pong => new Pong(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("host_time_us").GetInt64(),
            root.GetProperty("client_time_us").GetInt64()),

        ControlType.Stats => new ClientStats
        {
            FramesReceived = root.GetProperty("frames_received").GetInt64(),
            BytesReceived = root.GetProperty("bytes_received").GetInt64(),
            FramesMissing = OptionalInt64(root, "frames_missing") ?? 0,
            FramesDiscarded = OptionalInt64(root, "frames_discarded") ?? 0,
            FramesSubmitted = OptionalInt64(root, "frames_submitted") ?? 0,
            FramesDecoded = root.GetProperty("frames_decoded").GetInt64(),
            FramesRendered = root.GetProperty("frames_rendered").GetInt64(),
            FramesDisplayed = OptionalInt64(root, "frames_displayed") ?? 0,
            DecoderErrors = OptionalInt64(root, "decoder_errors") ?? 0,
            QueueDepth = (int)(OptionalInt64(root, "queue_depth") ?? 0),
            ReceiveLatency = ReadLatency(root, "receive_latency_ms"),
            RenderLatency = ReadLatency(root, "render_latency_ms"),
            DisplayLatency = ReadLatency(root, "display_latency_ms"),
        },

        ControlType.Error => new ErrorMessage(
            RequiredString(root, "code"),
            OptionalString(root, "message") ?? string.Empty,
            root.TryGetProperty("fatal", out JsonElement fatal) && fatal.GetBoolean()),

        ControlType.Bye => new Bye(OptionalString(root, "reason") ?? "unspecified"),

        _ => throw new ProtocolException(ErrorCode.MalformedMessage, $"Control type {type} has no reader."),
    };

    private static ClientCapabilities ReadCapabilities(JsonElement root)
    {
        JsonElement display = root.GetProperty("display");
        var decoders = new List<DecoderCapability>();
        foreach (JsonElement decoder in root.GetProperty("decoders").EnumerateArray())
        {
            // A decoder for a codec this build does not send is simply of no interest.
            if (OptionalString(decoder, "codec") is { } codec && codec != "h264")
            {
                continue;
            }

            decoders.Add(new DecoderCapability(
                RequiredString(decoder, "name"),
                decoder.GetProperty("hardware").GetBoolean(),
                decoder.TryGetProperty("low_latency", out JsonElement lowLatency) && lowLatency.GetBoolean(),
                decoder.GetProperty("max_width").GetInt32(),
                decoder.GetProperty("max_height").GetInt32()));
        }

        return new ClientCapabilities(
            display.GetProperty("width").GetInt32(),
            display.GetProperty("height").GetInt32(),
            display.TryGetProperty("refresh_hz", out JsonElement refresh) ? refresh.GetDouble() : 0,
            decoders);
    }

    private static void WriteLatency(Utf8JsonWriter json, string name, LatencySummary? latency)
    {
        if (latency is null)
        {
            return;
        }

        json.WriteStartObject(name);
        json.WriteNumber("count", latency.Count);
        json.WriteNumber("min", latency.MinMs);
        json.WriteNumber("mean", latency.MeanMs);
        json.WriteNumber("p95", latency.P95Ms);
        json.WriteNumber("max", latency.MaxMs);
        json.WriteEndObject();
    }

    private static LatencySummary? ReadLatency(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement latency) && latency.ValueKind == JsonValueKind.Object
            ? new LatencySummary(
                latency.GetProperty("count").GetInt32(),
                latency.GetProperty("min").GetDouble(),
                latency.GetProperty("mean").GetDouble(),
                latency.GetProperty("p95").GetDouble(),
                latency.GetProperty("max").GetDouble())
            : null;

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new FormatException($"'{name}' is null.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? OptionalInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}
