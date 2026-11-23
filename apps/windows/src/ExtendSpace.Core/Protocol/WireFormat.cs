using System.Buffers.Binary;

namespace ExtendSpace.Core.Protocol;

// The wire format of protocol draft 0 (protocol/specification/protocol.md), for the production
// host. Copied from the experiment's Streaming/Protocol.cs, which keeps its own copy until stage 3
// has been validated on a device (architecture section 12, stage 3, "Rollback").

/// <summary>The wire constants of the ExtendSpace protocol, draft 0. All integers on the wire are big-endian.</summary>
internal static class WireFormat
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

    // Pairing, on exsp-pair/0 connections only (transport architecture section 5).
    PairCommit = 11,
    PairNonce = 12,
    PairReveal = 13,
    PairResult = 14,
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

/// <summary>The error codes of protocol section 12.</summary>
internal static class WireErrorCode
{
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";
    public const string MalformedMessage = "malformed_message";
    public const string MessageTooLarge = "message_too_large";
    public const string UnexpectedMessage = "unexpected_message";
    public const string NotPaired = "not_paired";
    public const string AuthenticationFailed = "authentication_failed";
    public const string NoCommonConfiguration = "no_common_configuration";
    public const string StreamConfigRejected = "stream_config_rejected";
    public const string Timeout = "timeout";
    public const string Busy = "busy";
    public const string InternalError = "internal_error";

    /// <summary>Pairing: the commitment did not match, or the exchange was broken.</summary>
    public const string PairingFailed = "pairing_failed";

    /// <summary>Pairing: the tablet's user has not opened its pairing window.</summary>
    public const string PairingNotOpen = "pairing_not_open";

    /// <summary>Every code protocol section 12 defines.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ProtocolVersionUnsupported, MalformedMessage, MessageTooLarge, UnexpectedMessage, NotPaired,
        AuthenticationFailed, NoCommonConfiguration, StreamConfigRejected, Timeout, Busy, InternalError,
        PairingFailed, PairingNotOpen,
    };
}

/// <summary>The peer broke the protocol. <see cref="Code"/> is what an error message would carry.</summary>
internal sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
