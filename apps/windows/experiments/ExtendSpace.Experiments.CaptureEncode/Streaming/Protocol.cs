using System.Buffers.Binary;

namespace ExtendSpace.Experiments.CaptureEncode.Streaming;

/// <summary>
/// The wire constants of the ExtendSpace protocol, draft 0, as written down in
/// protocol/specification/protocol.md. All integers on the wire are big-endian.
/// </summary>
internal static class Protocol
{
    public const int MajorVersion = 0;

    public const int HeaderLength = 8;
    public const int VideoHeaderLength = 12;

    public const byte ChannelControl = 0;
    public const byte ChannelVideo = 1;
    public const byte ChannelInput = 2;

    public const int MaxControlPayload = 64 * 1024;
    public const int MaxVideoPayload = 8 * 1024 * 1024;
    public const int MaxInputPayload = 4 * 1024;

    /// <summary>Frames on a channel this build does not know are skipped, up to this size.</summary>
    public const int MaxSkippablePayload = MaxVideoPayload;

    public const int MaxJsonDepth = 8;

    /// <summary>The name of the Android app's local socket, which adb forwards a loopback port to.</summary>
    public const string LocalSocketName = "extendspace";

    public const int DefaultHostPort = 27183;

    public static readonly TimeSpan HandshakeStepTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan KeepaliveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ASCII "EXSP" and the framing version. Each side sends it first, before any frame.</summary>
    public static ReadOnlySpan<byte> Preface => "EXSP\0"u8;

    public static void WriteHeader(Span<byte> destination, FrameHeader header)
    {
        destination[0] = header.Channel;
        destination[1] = header.Type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], header.Flags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], header.Length);
    }

    public static FrameHeader ReadHeader(ReadOnlySpan<byte> source) => new(
        source[0],
        source[1],
        BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[4..]));
}

/// <param name="Length">Payload size in bytes. Unsigned, as on the wire: it is untrusted input.</param>
internal readonly record struct FrameHeader(byte Channel, byte Type, ushort Flags, uint Length);

internal enum ControlType : byte
{
    Hello = 1,
    Capabilities = 2,
    StreamConfig = 3,
    StreamReady = 4,
    RequestKeyframe = 5,
    Ping = 6,
    Pong = 7,
    Stats = 8,
    Error = 9,
    Bye = 10,
}

[Flags]
internal enum VideoFlags : ushort
{
    None = 0,

    /// <summary>The access unit is an IDR picture: decoding can start here.</summary>
    Keyframe = 1,

    /// <summary>The access unit begins with the SPS and PPS a decoder needs to be configured.</summary>
    Config = 2,
}

/// <summary>The error codes of protocol section 12 that this experiment can raise.</summary>
internal static class ErrorCode
{
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";
    public const string MalformedMessage = "malformed_message";
    public const string MessageTooLarge = "message_too_large";
    public const string UnexpectedMessage = "unexpected_message";
    public const string NoCommonConfiguration = "no_common_configuration";
    public const string StreamConfigRejected = "stream_config_rejected";
    public const string Timeout = "timeout";
    public const string InternalError = "internal_error";
}

/// <summary>The peer broke the protocol. <see cref="Code"/> is what an error message would carry.</summary>
internal sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
