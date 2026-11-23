namespace ExtendSpace.Media.Graphics;

/// <summary>
/// How a captured frame maps onto an encoded frame: which part of the source is used, how big the
/// encoded frame is, and where in it the picture lands. All sizes are in pixels.
/// </summary>
/// <param name="SourceWidth">Width of the source area that is used, starting at the top-left corner.</param>
/// <param name="OutputWidth">Width of the encoded frame. Always even, as 4:2:0 video requires.</param>
/// <param name="PictureX">Left edge of the picture inside the encoded frame; the rest is black.</param>
internal sealed record OutputGeometry(
    int SourceWidth,
    int SourceHeight,
    int OutputWidth,
    int OutputHeight,
    int PictureX,
    int PictureY,
    int PictureWidth,
    int PictureHeight)
{
    public const int MinimumOutputDimension = 128;
    public const int MaximumOutputDimension = 4096;

    /// <summary>True when the picture is resampled rather than copied pixel for pixel.</summary>
    public bool IsScaled => PictureWidth != SourceWidth || PictureHeight != SourceHeight;

    /// <summary>True when the picture does not fill the encoded frame and black bars make up the rest.</summary>
    public bool HasBars => PictureWidth != OutputWidth || PictureHeight != OutputHeight;

    /// <summary>
    /// With no requested size: the source at its own size, less one row or column if a dimension
    /// is odd. With a requested size: the whole source scaled to fit inside it, keeping its shape,
    /// centred. Null if the source is too small to encode at all.
    /// </summary>
    public static OutputGeometry? Create(int sourceWidth, int sourceHeight, (int Width, int Height)? requested)
    {
        if (requested is not { } output)
        {
            int width = sourceWidth & ~1;
            int height = sourceHeight & ~1;
            return width >= 2 && height >= 2
                ? new OutputGeometry(width, height, width, height, 0, 0, width, height)
                : null;
        }

        if (sourceWidth < 1 || sourceHeight < 1)
        {
            return null;
        }

        // Scale by whichever dimension is the tighter fit, then land on even numbers so that the
        // picture's edges fall on chroma sample boundaries.
        double scale = Math.Min((double)output.Width / sourceWidth, (double)output.Height / sourceHeight);
        int pictureWidth = Math.Clamp(RoundToEven(sourceWidth * scale), 2, output.Width);
        int pictureHeight = Math.Clamp(RoundToEven(sourceHeight * scale), 2, output.Height);
        int x = ((output.Width - pictureWidth) / 2) & ~1;
        int y = ((output.Height - pictureHeight) / 2) & ~1;

        return new OutputGeometry(sourceWidth, sourceHeight, output.Width, output.Height, x, y, pictureWidth, pictureHeight);
    }

    /// <summary>Whether a requested output size is one this experiment will try to encode.</summary>
    public static bool IsValidRequest(int width, int height) =>
        width % 2 == 0
        && height % 2 == 0
        && width is >= MinimumOutputDimension and <= MaximumOutputDimension
        && height is >= MinimumOutputDimension and <= MaximumOutputDimension;

    private static int RoundToEven(double value) => (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
}
