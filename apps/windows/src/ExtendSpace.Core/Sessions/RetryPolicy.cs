namespace ExtendSpace.Core.Sessions;

/// <summary>
/// How long to wait before each new connection attempt: 250 ms, doubling, capped at 4 s, and no
/// attempt once the budget has been used (protocol section 12). The experiment's
/// <c>ReconnectBackoff</c>, whose behaviour was measured on a device, moved here unchanged in
/// substance; the experiment keeps its own copy until stage 3.
/// </summary>
public sealed class RetryBackoff(TimeSpan budget)
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private TimeSpan _next = InitialDelay;

    public TimeSpan Budget => budget;

    /// <summary>
    /// The delay before the next attempt, or null when <paramref name="elapsed"/> (time since the
    /// budget began) has used it up. The last delay is shortened to end on the budget.
    /// </summary>
    public TimeSpan? NextDelay(TimeSpan elapsed)
    {
        TimeSpan remaining = budget - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan delay = _next < remaining ? _next : remaining;
        _next = _next * 2 < MaximumDelay ? _next * 2 : MaximumDelay;
        return delay;
    }

    public void Reset() => _next = InitialDelay;
}

/// <summary>
/// The host's choice of stream once it knows what the client can decode (decision D16): 1920 × 1080
/// at 60 fps and 12 Mbit/s if any listed decoder takes it, otherwise 1280 × 720 at 30 fps and
/// 6 Mbit/s, otherwise nothing. These are the only two configurations that have been measured on a
/// device. The source's size is offered to the policy but not used by this one: the pipeline scales
/// the picture to fit, as the experiment does.
/// </summary>
public static class StreamConfigurationPolicy
{
    public static readonly EncodingRequest Preferred = new(1920, 1080, 60, 12_000_000);
    public static readonly EncodingRequest Fallback = new(1280, 720, 30, 6_000_000);

    /// <summary>Null when no configuration fits: the session ends with <c>no_common_configuration</c>.</summary>
    public static EncodingRequest? Choose(VideoSourceDescription source, ClientCapabilities client)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (EncodingRequest candidate in (ReadOnlySpan<EncodingRequest>)[Preferred, Fallback])
        {
            if (client.CanDecode(candidate.Width, candidate.Height))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Extend (D89): the virtual monitor's own shape, as large as fits inside the measured envelope
    /// (1920 × 1080, or 1080 × 1920 for a portrait monitor), on even sizes; then the same inside
    /// 1280 × 720 at 30 fps. A 16:9 monitor gets exactly D16's two configurations.
    /// </summary>
    public static EncodingRequest? ChooseShaped(VideoSourceDescription source, ClientCapabilities client)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (EncodingRequest envelope in (ReadOnlySpan<EncodingRequest>)[Preferred, Fallback])
        {
            (int width, int height) = Shape(source.Width, source.Height, envelope.Width, envelope.Height);
            if (client.CanDecode(width, height))
            {
                return envelope with { Width = width, Height = height };
            }
        }

        return null;
    }

    /// <summary>The largest even size of the source's shape inside the envelope, turned to the source's orientation.</summary>
    internal static (int Width, int Height) Shape(int sourceWidth, int sourceHeight, int envelopeWidth, int envelopeHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (envelopeWidth, envelopeHeight);
        }

        (int boxWidth, int boxHeight) = sourceHeight > sourceWidth ? (envelopeHeight, envelopeWidth) : (envelopeWidth, envelopeHeight);
        if (sourceWidth <= boxWidth && sourceHeight <= boxHeight)
        {
            return (Math.Max(128, sourceWidth & ~1), Math.Max(128, sourceHeight & ~1));
        }

        // Integer arithmetic, so that 2560 × 1600 gives exactly 1728 × 1080.
        long width, height;
        if ((long)sourceWidth * boxHeight <= (long)sourceHeight * boxWidth)
        {
            height = boxHeight;
            width = (long)sourceWidth * boxHeight / sourceHeight;
        }
        else
        {
            width = boxWidth;
            height = (long)sourceHeight * boxWidth / sourceWidth;
        }

        return (Math.Max(128, (int)width & ~1), Math.Max(128, (int)height & ~1));
    }
}
