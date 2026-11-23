namespace ExtendSpace.Media.Encoding;

/// <summary>
/// Holds the encoder to the stream the client was told about (decision D57). <c>stream_config</c> is
/// sent before the encoder has produced anything, from the settings it was opened with and accepted;
/// every sequence parameter set it then produces is read and compared with them. A picture of another
/// size or profile, or an interlaced one, is never handed on: the session fails with
/// <see cref="MediaFaultCodes.EncoderOutputMismatch"/> instead. A frame rate signalled in the stream is
/// recorded, not enforced: it is informative, and the rate actually sent is what the pacer lets through.
/// </summary>
internal sealed class EncodedOutputCheck(int width, int height, H264Profile profile)
{
    private readonly int _profileIdc = profile switch
    {
        H264Profile.Baseline => H264SequenceParameterSet.BaselineProfileIdc,
        H264Profile.Main => H264SequenceParameterSet.MainProfileIdc,
        _ => H264SequenceParameterSet.HighProfileIdc,
    };

    /// <summary>The first parameter set seen, once one has passed the check.</summary>
    public H264SequenceParameterSet? First { get; private set; }

    /// <summary>Parameter sets checked so far.</summary>
    public long Checked { get; private set; }

    /// <summary>Returns quietly for an access unit without an SPS, or with one that matches.</summary>
    /// <exception cref="MediaEncoderException">The SPS describes another stream, or cannot be read.</exception>
    public void Verify(ReadOnlySpan<byte> accessUnit)
    {
        H264SequenceParameterSet? produced;
        try
        {
            produced = H264SequenceParameterSet.FindIn(accessUnit);
        }
        catch (FormatException exception)
        {
            throw new MediaEncoderException(MediaFaultCodes.EncoderOutputMismatch, $"The encoder produced a sequence parameter set that cannot be read: {exception.Message}", exception);
        }

        if (produced is null)
        {
            return;
        }

        if (produced.Width != width || produced.Height != height || produced.ProfileIdc != _profileIdc || produced.Interlaced)
        {
            throw new MediaEncoderException(
                MediaFaultCodes.EncoderOutputMismatch,
                $"The encoder produced {produced}, but the client was told {width} x {height}, {profile} profile, progressive.");
        }

        Checked++;
        First ??= produced;
    }
}
