namespace ExtendSpace.Core.Protocol;

// Copied from the experiment's Streaming/FrameParser.cs for the production host; the experiment keeps its own
// copy until stage 3 has been validated on a device (architecture section 12, stage 3).

/// <param name="payload">Valid only during the call.</param>
internal delegate void FrameHandler(FrameHeader header, ReadOnlySpan<byte> payload);

/// <summary>
/// Turns the bytes of a connection, in whatever pieces they arrive, into protocol frames. It knows
/// nothing about sockets, so every way a stream can be cut up or corrupted can be tested directly.
///
/// Everything it is given is untrusted. A length is checked against its channel's limit before any
/// memory is set aside for it, so a hostile or broken peer cannot make this allocate more than the
/// largest legitimate frame.
/// </summary>
internal sealed class FrameParser(int maxVideoPayload = WireFormat.MaxVideoPayload)
{
    private enum Stage
    {
        Preface,
        Header,
        Payload,
        Skipping,
    }

    private readonly byte[] _fixed = new byte[WireFormat.HeaderLength];
    private readonly int _maxVideoPayload = Math.Min(maxVideoPayload, WireFormat.MaxVideoPayload);

    private Stage _stage = Stage.Preface;
    private int _fixedFilled;
    private FrameHeader _header;
    private byte[] _payload = [];
    private int _payloadFilled;
    private long _toSkip;

    /// <summary>True once the peer's preface has arrived and was correct.</summary>
    public bool PrefaceReceived => _stage != Stage.Preface;

    /// <summary>
    /// True when the bytes so far end exactly between frames. A connection that closes while this
    /// is false was cut off in the middle of something.
    /// </summary>
    public bool IsAtFrameBoundary => _stage == Stage.Header && _fixedFilled == 0;

    public long FramesParsed { get; private set; }

    public long FramesSkipped { get; private set; }

    /// <summary>
    /// Consumes <paramref name="data"/> and calls <paramref name="handler"/> once for each frame it
    /// completes, in order.
    /// </summary>
    /// <exception cref="ProtocolException">The bytes are not a valid ExtendSpace stream.</exception>
    public void Append(ReadOnlySpan<byte> data, FrameHandler handler)
    {
        while (!data.IsEmpty)
        {
            switch (_stage)
            {
                case Stage.Preface:
                    data = FillFixed(data, WireFormat.Preface.Length);
                    CheckPreface();
                    break;

                case Stage.Header:
                    data = FillFixed(data, WireFormat.HeaderLength);
                    if (_fixedFilled == WireFormat.HeaderLength)
                    {
                        _fixedFilled = 0;
                        BeginFrame(WireFormat.ReadHeader(_fixed), handler);
                    }

                    break;

                case Stage.Payload:
                {
                    int take = Math.Min(data.Length, (int)_header.Length - _payloadFilled);
                    data[..take].CopyTo(_payload.AsSpan(_payloadFilled));
                    _payloadFilled += take;
                    data = data[take..];
                    if (_payloadFilled == _header.Length)
                    {
                        Emit(handler, _payload.AsSpan(0, _payloadFilled));
                    }

                    break;
                }

                case Stage.Skipping:
                {
                    int take = (int)Math.Min(data.Length, _toSkip);
                    _toSkip -= take;
                    data = data[take..];
                    if (_toSkip == 0)
                    {
                        FramesSkipped++;
                        _stage = Stage.Header;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>Call when the connection has closed. Throws if it closed part-way through a frame.</summary>
    public void Complete()
    {
        if (!IsAtFrameBoundary)
        {
            throw new ProtocolException(
                WireErrorCode.MalformedMessage,
                _stage == Stage.Preface
                    ? "The connection closed before the peer finished its preface."
                    : "The connection closed in the middle of a frame.");
        }
    }

    private ReadOnlySpan<byte> FillFixed(ReadOnlySpan<byte> data, int needed)
    {
        int take = Math.Min(data.Length, needed - _fixedFilled);
        data[..take].CopyTo(_fixed.AsSpan(_fixedFilled));
        _fixedFilled += take;
        return data[take..];
    }

    private void CheckPreface()
    {
        // Reject on the first wrong byte: there is no reason to wait for the rest of a stranger's data.
        if (!_fixed.AsSpan(0, _fixedFilled).SequenceEqual(WireFormat.Preface[.._fixedFilled]))
        {
            throw new ProtocolException(WireErrorCode.MalformedMessage, "The peer did not send the ExtendSpace preface.");
        }

        if (_fixedFilled == WireFormat.Preface.Length)
        {
            _fixedFilled = 0;
            _stage = Stage.Header;
        }
    }

    private void BeginFrame(FrameHeader header, FrameHandler handler)
    {
        int? limit = header.Channel switch
        {
            WireFormat.ChannelControl => WireFormat.MaxControlPayload,
            WireFormat.ChannelVideo => _maxVideoPayload,
            WireFormat.ChannelInput => WireFormat.MaxInputPayload,
            _ => null,
        };

        if (header.Length > (uint)(limit ?? WireFormat.MaxSkippablePayload))
        {
            throw new ProtocolException(
                WireErrorCode.MessageTooLarge,
                $"A frame on channel {header.Channel} announces {header.Length} bytes; the limit is {limit ?? WireFormat.MaxSkippablePayload}.");
        }

        _header = header;
        if (limit is null)
        {
            // An unknown channel: pass over it without keeping a byte.
            _toSkip = header.Length;
            if (_toSkip == 0)
            {
                FramesSkipped++;
            }
            else
            {
                _stage = Stage.Skipping;
            }

            return;
        }

        if (header.Length == 0)
        {
            Emit(handler, ReadOnlySpan<byte>.Empty);
            return;
        }

        if (_payload.Length < header.Length)
        {
            // Sized to the frame, which is within its limit, and kept for the frames that follow.
            _payload = new byte[header.Length];
        }

        _payloadFilled = 0;
        _stage = Stage.Payload;
    }

    private void Emit(FrameHandler handler, ReadOnlySpan<byte> payload)
    {
        _stage = Stage.Header;
        FramesParsed++;
        handler(_header, payload);
    }
}
