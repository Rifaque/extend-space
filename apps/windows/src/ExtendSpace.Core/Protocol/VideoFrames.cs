using System.Buffers;
using System.Buffers.Binary;

namespace ExtendSpace.Core.Protocol;

// Copied from the experiment's Streaming/VideoFrames.cs for the production host; the experiment keeps its own
// copy until stage 3 has been validated on a device (architecture section 12, stage 3).

/// <summary>One video frame, complete with its headers, in a rented buffer: ready to write to a socket.</summary>
internal sealed class OutgoingVideoFrame
{
    private byte[]? _buffer;

    private OutgoingVideoFrame(byte[] buffer, int length, uint sequence, long timestampMicros, VideoFlags flags)
    {
        _buffer = buffer;
        Length = length;
        Sequence = sequence;
        TimestampMicros = timestampMicros;
        Flags = flags;
    }

    public int Length { get; }

    public uint Sequence { get; }

    public long TimestampMicros { get; }

    public VideoFlags Flags { get; }

    /// <summary>A decoder can start here: an IDR picture carrying its SPS and PPS.</summary>
    public bool IsRandomAccessPoint => Flags.HasFlag(VideoFlags.Keyframe | VideoFlags.Config);

    public ReadOnlySpan<byte> Bytes => Memory.Span;

    /// <summary>The same bytes, for an asynchronous write. Valid until <see cref="Release"/>.</summary>
    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(OutgoingVideoFrame))).AsMemory(0, Length);

    /// <summary>
    /// Builds the frame: 8-byte frame header, 12-byte video header, then <paramref name="parameterSets"/>
    /// (possibly empty) followed by <paramref name="accessUnit"/>, which together are one Annex B access unit.
    /// </summary>
    public static OutgoingVideoFrame Create(
        uint sequence,
        long timestampMicros,
        VideoFlags flags,
        ReadOnlySpan<byte> parameterSets,
        ReadOnlySpan<byte> accessUnit)
    {
        int payload = WireFormat.VideoHeaderLength + parameterSets.Length + accessUnit.Length;
        int total = WireFormat.HeaderLength + payload;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);

        Span<byte> span = buffer;
        WireFormat.WriteHeader(span, new FrameHeader(WireFormat.ChannelVideo, 0, (ushort)flags, (uint)payload));
        VideoFrameCodec.WriteVideoHeader(span[WireFormat.HeaderLength..], sequence, timestampMicros);
        int offset = WireFormat.HeaderLength + WireFormat.VideoHeaderLength;
        parameterSets.CopyTo(span[offset..]);
        accessUnit.CopyTo(span[(offset + parameterSets.Length)..]);

        return new OutgoingVideoFrame(buffer, total, sequence, timestampMicros, flags);
    }

    /// <summary>Gives the buffer back. Call exactly once, when the frame has been written or dropped.</summary>
    public void Release()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal static class VideoFrameCodec
{
    public static void WriteVideoHeader(Span<byte> destination, uint sequence, long timestampMicros)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, sequence);
        BinaryPrimitives.WriteInt64BigEndian(destination[4..], timestampMicros);
    }

    /// <summary>Splits a video payload into its header fields and the access unit behind them.</summary>
    /// <exception cref="ProtocolException">The payload is too short to hold a video header and any data.</exception>
    public static ReadOnlySpan<byte> ReadVideoPayload(ReadOnlySpan<byte> payload, out uint sequence, out long timestampMicros)
    {
        if (payload.Length <= WireFormat.VideoHeaderLength)
        {
            throw new ProtocolException(
                WireErrorCode.MalformedMessage,
                $"A video frame of {payload.Length} bytes is too short to hold its {WireFormat.VideoHeaderLength}-byte header and an access unit.");
        }

        sequence = BinaryPrimitives.ReadUInt32BigEndian(payload);
        timestampMicros = BinaryPrimitives.ReadInt64BigEndian(payload[4..]);
        return payload[WireFormat.VideoHeaderLength..];
    }
}

/// <summary>
/// Decides the flags of each encoded access unit and makes sure every keyframe that is sent can
/// configure a decoder. Encoders put SPS and PPS ahead of their own periodic keyframes, but that is
/// their choice; when a keyframe arrives without them, the last ones seen are put in front of it.
/// </summary>
internal sealed class ParameterSetTracker
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private byte[] _latest = [];

    /// <summary>True once an SPS and a PPS have been seen, so that any later keyframe can be made self-contained.</summary>
    public bool HasParameterSets => _latest.Length > 0;

    /// <summary>
    /// Returns the flags for <paramref name="accessUnit"/> and, in <paramref name="prepend"/>, the
    /// parameter sets that must be sent in front of it (empty when it already carries its own or
    /// does not need any).
    /// </summary>
    public VideoFlags Classify(ReadOnlySpan<byte> accessUnit, out ReadOnlySpan<byte> prepend)
    {
        prepend = default;
        NalUnit? sps = null;
        NalUnit? pps = null;
        bool isIdr = false;

        foreach (NalUnit unit in AnnexB.FindNalUnits(accessUnit))
        {
            int type = AnnexB.NalType(accessUnit[unit.Offset]);
            if (H264NalType.IsVcl(type))
            {
                isIdr = type == H264NalType.IdrSlice;
                break;
            }

            if (type == H264NalType.Sps)
            {
                sps ??= unit;
            }
            else if (type == H264NalType.Pps)
            {
                pps ??= unit;
            }
        }

        if (sps is { } foundSps && pps is { } foundPps)
        {
            Remember(accessUnit.Slice(foundSps.Offset, foundSps.Length), accessUnit.Slice(foundPps.Offset, foundPps.Length));
            return isIdr ? VideoFlags.Keyframe | VideoFlags.Config : VideoFlags.None;
        }

        if (!isIdr)
        {
            return VideoFlags.None;
        }

        if (_latest.Length == 0)
        {
            // A keyframe nobody could start decoding from. It is flagged honestly and not as CONFIG.
            return VideoFlags.Keyframe;
        }

        prepend = _latest;
        return VideoFlags.Keyframe | VideoFlags.Config;
    }

    private void Remember(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        int length = (2 * StartCode.Length) + sps.Length + pps.Length;
        if (_latest.Length != length)
        {
            _latest = new byte[length];
        }

        Span<byte> destination = _latest;
        StartCode.CopyTo(destination);
        sps.CopyTo(destination[StartCode.Length..]);
        StartCode.CopyTo(destination[(StartCode.Length + sps.Length)..]);
        pps.CopyTo(destination[((2 * StartCode.Length) + sps.Length)..]);
    }
}
