using System.Buffers;
using System.Text.Json;

namespace ExtendSpace.Core.Protocol;

// The control messages of protocol draft 0 (sections 8 to 14), for the production host. Taken from
// the experiment's Streaming/ControlMessages.cs, with one deliberate difference: decoding is strict.
// A field this build knows that arrives with the wrong type or an impossible value is a
// malformed_message, where the experiment read some of them as absent or as zero. Unknown fields
// and unknown message types are still ignored, as protocol sections 4 and 5 require. The names
// carry a "Message" suffix so that they cannot be confused with the session records of the same
// meaning in ExtendSpace.Core.Sessions.

/// <summary>First message in each direction: which protocol versions the sender speaks, and who it is.</summary>
/// <param name="Role">"host" or "client"; empty when the sender left it out.</param>
/// <param name="Purpose">"pair" on a pairing connection (transport architecture section 5); null on a session.</param>
internal sealed record HelloMessage(int ProtocolMin, int ProtocolMax, string Role, string DeviceName, string AppVersion, string? Purpose = null);

internal sealed record DecoderEntry(string Name, bool Hardware, bool LowLatency, int MaxWidth, int MaxHeight);

/// <summary>What the client can show and decode. The host chooses within it.</summary>
/// <param name="RefreshHz">Zero when the client did not say.</param>
internal sealed record CapabilitiesMessage(int DisplayWidth, int DisplayHeight, double RefreshHz, IReadOnlyList<DecoderEntry> Decoders);

/// <summary>What the host is about to send.</summary>
internal sealed record StreamConfigMessage(
    string Codec,
    string Profile,
    int Width,
    int Height,
    int FrameRate,
    int Bitrate,
    int MaxVideoPayload,
    string? Encoder,
    string? EncoderAcceleration,
    bool InputTouch = false,
    string? Mode = null);

// Pairing (transport architecture section 5). Every byte field is exactly 32 bytes, base64 on the wire.
internal sealed record PairCommitMessage(byte[] Commitment);

internal sealed record PairNonceMessage(byte[] Nonce);

internal sealed record PairRevealMessage(byte[] Nonce);

internal sealed record PairResultMessage(bool Accepted);

internal sealed record StreamReadyMessage(string? Decoder, bool? Hardware);

internal sealed record KeyframeRequestMessage(string Reason);

/// <param name="HostTimeMicros">The host's monotonic clock when the ping was written.</param>
/// <param name="ClockOffsetMicros">The host's estimate of "client clock minus host clock"; absent until a pong has come back.</param>
/// <param name="RoundTripMicros">The round trip the estimate was taken from.</param>
internal sealed record PingMessage(long Id, long HostTimeMicros, long? ClockOffsetMicros, long? RoundTripMicros);

/// <param name="ClientTimeMicros">The client's monotonic clock when it answered.</param>
internal sealed record PongMessage(long Id, long HostTimeMicros, long ClientTimeMicros);

/// <summary>Delays over one reporting interval, in milliseconds.</summary>
internal sealed record LatencySummary(int Count, double MinMs, double MeanMs, double P95Ms, double MaxMs);

/// <summary>The client's counters for the connection and its latest latency figures.</summary>
internal sealed record StatsMessage
{
    public long FramesReceived { get; init; }

    public long BytesReceived { get; init; }

    public long FramesMissing { get; init; }

    public long FramesDiscarded { get; init; }

    public long FramesSubmitted { get; init; }

    public long FramesDecoded { get; init; }

    public long FramesRendered { get; init; }

    public long FramesDisplayed { get; init; }

    public long DecoderErrors { get; init; }

    public int QueueDepth { get; init; }

    public LatencySummary? ReceiveLatency { get; init; }

    public LatencySummary? RenderLatency { get; init; }

    public LatencySummary? DisplayLatency { get; init; }
}

internal sealed record ErrorMessage(string Code, string Message, bool Fatal);

internal sealed record ByeMessage(string Reason);

/// <summary>
/// Control messages are small UTF-8 JSON objects (protocol section 4). Unknown fields are ignored;
/// a missing required field, a known field of the wrong type or with an impossible value, or nesting
/// deeper than eight levels is a <c>malformed_message</c>.
/// </summary>
internal static class ControlCodec
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = WireFormat.MaxJsonDepth };

    /// <summary>A complete control frame: header and JSON payload.</summary>
    public static byte[] Encode(object message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(WireFormat.HeaderLength);
        buffer.Advance(WireFormat.HeaderLength);

        ControlType type;
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            type = Write(json, message);
            json.WriteEndObject();
        }

        byte[] frame = buffer.WrittenSpan.ToArray();
        int length = frame.Length - WireFormat.HeaderLength;
        if (length > WireFormat.MaxControlPayload)
        {
            throw new InvalidOperationException($"A {type} message of {length} bytes exceeds the control channel limit.");
        }

        WireFormat.WriteHeader(frame, new FrameHeader(WireFormat.ChannelControl, (byte)type, 0, (uint)length));
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
                throw new ProtocolException(WireErrorCode.MalformedMessage, $"A {(ControlType)type} message is not a JSON object.");
            }

            return Read((ControlType)type, root);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(WireErrorCode.MalformedMessage, $"A {(ControlType)type} message is not valid JSON: {exception.Message}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new ProtocolException(WireErrorCode.MalformedMessage, $"A {(ControlType)type} message has a missing or mistyped field: {exception.Message}");
        }
    }

    private static ControlType Write(Utf8JsonWriter json, object message)
    {
        switch (message)
        {
            case HelloMessage hello:
                json.WriteNumber("protocol_min", hello.ProtocolMin);
                json.WriteNumber("protocol_max", hello.ProtocolMax);
                json.WriteString("role", hello.Role);
                json.WriteString("device_name", hello.DeviceName);
                json.WriteString("app_version", hello.AppVersion);
                if (hello.Purpose is not null)
                {
                    json.WriteString("purpose", hello.Purpose);
                }

                return ControlType.Hello;

            case CapabilitiesMessage capabilities:
                json.WriteStartObject("display");
                json.WriteNumber("width", capabilities.DisplayWidth);
                json.WriteNumber("height", capabilities.DisplayHeight);
                json.WriteNumber("refresh_hz", capabilities.RefreshHz);
                json.WriteEndObject();
                json.WriteStartArray("decoders");
                foreach (DecoderEntry decoder in capabilities.Decoders)
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

            case StreamConfigMessage config:
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

                if (config.Mode is not null)
                {
                    // D90: "extend" when the stream is a virtual monitor; absent means mirror.
                    json.WriteString("mode", config.Mode);
                }

                if (config.InputTouch)
                {
                    // D81: the client may send touch frames on this connection.
                    json.WriteStartObject("input");
                    json.WriteBoolean("touch", true);
                    json.WriteEndObject();
                }

                return ControlType.StreamConfig;

            case PairCommitMessage commit:
                json.WriteString("commitment", Convert.ToBase64String(Exactly32(commit.Commitment)));
                return ControlType.PairCommit;

            case PairNonceMessage nonce:
                json.WriteString("nonce", Convert.ToBase64String(Exactly32(nonce.Nonce)));
                return ControlType.PairNonce;

            case PairRevealMessage reveal:
                json.WriteString("nonce", Convert.ToBase64String(Exactly32(reveal.Nonce)));
                return ControlType.PairReveal;

            case PairResultMessage result:
                json.WriteBoolean("accepted", result.Accepted);
                return ControlType.PairResult;

            case StreamReadyMessage ready:
                if (ready.Decoder is not null)
                {
                    json.WriteString("decoder", ready.Decoder);
                }

                if (ready.Hardware is { } hardware)
                {
                    json.WriteBoolean("hardware", hardware);
                }

                return ControlType.StreamReady;

            case KeyframeRequestMessage request:
                json.WriteString("reason", request.Reason);
                return ControlType.RequestKeyframe;

            case PingMessage ping:
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

            case PongMessage pong:
                json.WriteNumber("id", pong.Id);
                json.WriteNumber("host_time_us", pong.HostTimeMicros);
                json.WriteNumber("client_time_us", pong.ClientTimeMicros);
                return ControlType.Pong;

            case StatsMessage stats:
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

            case ByeMessage bye:
                json.WriteString("reason", bye.Reason);
                return ControlType.Bye;

            default:
                throw new ArgumentException($"{message.GetType().Name} is not a control message.", nameof(message));
        }
    }

    private static object Read(ControlType type, JsonElement root) => type switch
    {
        ControlType.Hello => ReadHello(root),

        ControlType.Capabilities => ReadCapabilities(root),

        ControlType.StreamConfig => new StreamConfigMessage(
            RequiredString(root, "codec"),
            RequiredString(root, "profile"),
            Positive(root, "width"),
            Positive(root, "height"),
            Positive(root, "frame_rate"),
            Positive(root, "bitrate"),
            Positive(root, "max_video_payload"),
            OptionalString(root, "encoder"),
            OptionalString(root, "encoder_acceleration"),
            ReadInputTouch(root),
            OptionalString(root, "mode")),

        ControlType.PairCommit => new PairCommitMessage(Base64Of32(root, "commitment")),

        ControlType.PairNonce => new PairNonceMessage(Base64Of32(root, "nonce")),

        ControlType.PairReveal => new PairRevealMessage(Base64Of32(root, "nonce")),

        ControlType.PairResult => new PairResultMessage(root.GetProperty("accepted").GetBoolean()),

        ControlType.StreamReady => new StreamReadyMessage(OptionalString(root, "decoder"), OptionalBoolean(root, "hardware")),

        ControlType.RequestKeyframe => new KeyframeRequestMessage(OptionalString(root, "reason") ?? "unspecified"),

        ControlType.Ping => new PingMessage(
            NonNegative64(root, "id"),
            root.GetProperty("host_time_us").GetInt64(),
            OptionalInt64(root, "clock_offset_us"),
            OptionalInt64(root, "round_trip_us")),

        ControlType.Pong => new PongMessage(
            NonNegative64(root, "id"),
            root.GetProperty("host_time_us").GetInt64(),
            root.GetProperty("client_time_us").GetInt64()),

        ControlType.Stats => new StatsMessage
        {
            FramesReceived = NonNegative64(root, "frames_received"),
            BytesReceived = NonNegative64(root, "bytes_received"),
            FramesMissing = OptionalNonNegative64(root, "frames_missing") ?? 0,
            FramesDiscarded = OptionalNonNegative64(root, "frames_discarded") ?? 0,
            FramesSubmitted = OptionalNonNegative64(root, "frames_submitted") ?? 0,
            FramesDecoded = NonNegative64(root, "frames_decoded"),
            FramesRendered = NonNegative64(root, "frames_rendered"),
            FramesDisplayed = OptionalNonNegative64(root, "frames_displayed") ?? 0,
            DecoderErrors = OptionalNonNegative64(root, "decoder_errors") ?? 0,
            QueueDepth = checked((int)(OptionalNonNegative64(root, "queue_depth") ?? 0)),
            ReceiveLatency = ReadLatency(root, "receive_latency_ms"),
            RenderLatency = ReadLatency(root, "render_latency_ms"),
            DisplayLatency = ReadLatency(root, "display_latency_ms"),
        },

        ControlType.Error => new ErrorMessage(
            RequiredString(root, "code"),
            OptionalString(root, "message") ?? string.Empty,
            OptionalBoolean(root, "fatal") ?? false),

        ControlType.Bye => new ByeMessage(OptionalString(root, "reason") ?? "unspecified"),

        _ => throw new ProtocolException(WireErrorCode.MalformedMessage, $"Control type {type} has no reader."),
    };

    private static HelloMessage ReadHello(JsonElement root)
    {
        int min = root.GetProperty("protocol_min").GetInt32();
        int max = root.GetProperty("protocol_max").GetInt32();
        if (min < 0 || max < min)
        {
            throw new FormatException($"protocol_min {min} and protocol_max {max} are not a range of versions.");
        }

        return new HelloMessage(
            min,
            max,
            OptionalString(root, "role") ?? string.Empty,
            OptionalString(root, "device_name") ?? string.Empty,
            OptionalString(root, "app_version") ?? string.Empty,
            OptionalString(root, "purpose"));
    }

    private static bool ReadInputTouch(JsonElement root)
    {
        if (!root.TryGetProperty("input", out JsonElement input))
        {
            return false;
        }

        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("'input' is not an object.");
        }

        return OptionalBoolean(input, "touch") ?? false;
    }

    private static byte[] Base64Of32(JsonElement root, string name)
    {
        byte[] value = Convert.FromBase64String(RequiredString(root, name));
        return value.Length == 32 ? value : throw new FormatException($"'{name}' is {value.Length} bytes; it must be 32.");
    }

    private static byte[] Exactly32(byte[] value) =>
        value is { Length: 32 } ? value : throw new ArgumentException("Pairing values are exactly 32 bytes.", nameof(value));

    private static CapabilitiesMessage ReadCapabilities(JsonElement root)
    {
        JsonElement display = Required(root, "display", JsonValueKind.Object);
        double refresh = 0;
        if (display.TryGetProperty("refresh_hz", out JsonElement refreshElement))
        {
            refresh = refreshElement.GetDouble();
            if (!double.IsFinite(refresh) || refresh < 0)
            {
                throw new FormatException($"'refresh_hz' is {refresh}.");
            }
        }

        var decoders = new List<DecoderEntry>();
        foreach (JsonElement decoder in Required(root, "decoders", JsonValueKind.Array).EnumerateArray())
        {
            if (decoder.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("An entry of 'decoders' is not an object.");
            }

            // A decoder for a codec this build does not send is of no interest, but it is still
            // checked for being what the protocol says it is.
            string? codec = OptionalString(decoder, "codec");
            string name = RequiredString(decoder, "name");
            if (name.Length == 0)
            {
                throw new FormatException("A decoder has an empty 'name'.");
            }

            var entry = new DecoderEntry(
                name,
                decoder.GetProperty("hardware").GetBoolean(),
                OptionalBoolean(decoder, "low_latency") ?? false,
                Positive(decoder, "max_width"),
                Positive(decoder, "max_height"));
            if (codec is null or "h264")
            {
                decoders.Add(entry);
            }
        }

        return new CapabilitiesMessage(Positive(display, "width"), Positive(display, "height"), refresh, decoders);
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

    private static LatencySummary? ReadLatency(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement latency))
        {
            return null;
        }

        if (latency.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"'{name}' is not an object.");
        }

        return new LatencySummary(
            checked((int)NonNegative64(latency, "count")),
            latency.GetProperty("min").GetDouble(),
            latency.GetProperty("mean").GetDouble(),
            latency.GetProperty("p95").GetDouble(),
            latency.GetProperty("max").GetDouble());
    }

    private static JsonElement Required(JsonElement element, string name, JsonValueKind kind)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind == kind ? value : throw new FormatException($"'{name}' is {value.ValueKind}, not {kind}.");
    }

    private static string RequiredString(JsonElement element, string name) => Required(element, name, JsonValueKind.String).GetString()!;

    private static int Positive(JsonElement element, string name)
    {
        int value = element.GetProperty(name).GetInt32();
        return value > 0 ? value : throw new FormatException($"'{name}' is {value}; it must be positive.");
    }

    private static long NonNegative64(JsonElement element, string name)
    {
        long value = element.GetProperty(name).GetInt64();
        return value >= 0 ? value : throw new FormatException($"'{name}' is {value}; it cannot be negative.");
    }

    private static long? OptionalNonNegative64(JsonElement element, string name) =>
        element.TryGetProperty(name, out _) ? NonNegative64(element, name) : null;

    // Optional fields may be absent. Present, they must have the type the protocol gives them:
    // GetString, GetBoolean and GetInt64 throw InvalidOperationException otherwise, which Decode
    // reports as malformed_message.
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? throw new FormatException($"'{name}' is null.") : null;

    private static bool? OptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : null;

    private static long? OptionalInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : null;
}
