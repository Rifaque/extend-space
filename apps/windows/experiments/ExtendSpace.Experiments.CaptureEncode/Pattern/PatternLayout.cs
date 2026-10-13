namespace ExtendSpace.Experiments.CaptureEncode.Pattern;

/// <summary>A filled rectangle in window pixels; right and bottom are exclusive.</summary>
internal readonly record struct Box(int Left, int Top, int Right, int Bottom);

/// <summary>The few colours the pattern is drawn in.</summary>
internal enum PatternColor
{
    Black,
    White,
    Red,
    Green,
    Blue,
    Yellow,
    Cyan,
    Magenta,
    DarkGrey,
    MidGrey,
}

/// <summary>
/// What the test pattern shows for one frame, as coloured boxes. Everything in it follows from the
/// frame number and the elapsed time, so a photograph of a screen can be read back:
///
///   - a clock of six seven-segment digits: elapsed milliseconds, last six digits;
///   - a smaller frame counter beneath it;
///   - a row of cells in which one lit cell steps along by one per frame;
///   - a block that crosses the window once a second, so its position is the millisecond;
///   - a mosaic that changes completely on every frame, to give an encoder something to do.
///
/// The colour bars along the top never change.
/// </summary>
internal static class PatternLayout
{
    public const int ClockDigits = 6;
    public const int CounterDigits = 6;
    public const int TickCells = 30;
    public const int MosaicColumns = 64;
    public const int MosaicRows = 9;

    private static readonly PatternColor[] Bars =
    [
        PatternColor.White, PatternColor.Yellow, PatternColor.Cyan, PatternColor.Green,
        PatternColor.Magenta, PatternColor.Red, PatternColor.Blue, PatternColor.MidGrey,
    ];

    private static readonly PatternColor[] MosaicPalette =
    [
        PatternColor.Black, PatternColor.White, PatternColor.Red, PatternColor.Green,
        PatternColor.Blue, PatternColor.Yellow, PatternColor.Cyan, PatternColor.Magenta,
    ];

    /// <summary>Adds the boxes for one frame to <paramref name="boxes"/>, later ones drawn over earlier ones.</summary>
    public static void Build(int width, int height, long frame, long elapsedMilliseconds, List<(PatternColor Color, Box Box)> boxes)
    {
        boxes.Clear();

        // Static colour bars.
        int barsBottom = height * 12 / 100;
        for (int i = 0; i < Bars.Length; i++)
        {
            boxes.Add((Bars[i], new Box(width * i / Bars.Length, 0, width * (i + 1) / Bars.Length, barsBottom)));
        }

        // The clock, on a black panel.
        int clockTop = height * 16 / 100;
        int clockHeight = height * 26 / 100;
        boxes.Add((PatternColor.Black, new Box(0, clockTop - (height * 2 / 100), width, clockTop + clockHeight + (height * 2 / 100))));
        AddNumber(boxes, PatternColor.White, elapsedMilliseconds, ClockDigits, width, clockTop, clockHeight);

        // The frame counter.
        int counterTop = height * 47 / 100;
        int counterHeight = height * 9 / 100;
        AddNumber(boxes, PatternColor.Yellow, frame, CounterDigits, width, counterTop, counterHeight);

        // The stepping cell.
        int ticksTop = height * 60 / 100;
        int ticksBottom = height * 66 / 100;
        int lit = TickCell(frame);
        for (int i = 0; i < TickCells; i++)
        {
            int left = width * i / TickCells;
            int right = width * (i + 1) / TickCells;
            boxes.Add((i == lit ? PatternColor.White : PatternColor.Black, new Box(left + 2, ticksTop, right - 2, ticksBottom)));
        }

        // The sweep.
        int sweepTop = height * 68 / 100;
        int sweepBottom = height * 74 / 100;
        int sweepWidth = Math.Max(4, width / 50);
        int sweepLeft = SweepLeft(width, elapsedMilliseconds);
        boxes.Add((PatternColor.Black, new Box(0, sweepTop, width, sweepBottom)));
        boxes.Add((PatternColor.White, new Box(sweepLeft, sweepTop, Math.Min(width, sweepLeft + sweepWidth), sweepBottom)));

        // The mosaic.
        int mosaicTop = height * 76 / 100;
        for (int row = 0; row < MosaicRows; row++)
        {
            int top = mosaicTop + ((height - mosaicTop) * row / MosaicRows);
            int bottom = mosaicTop + ((height - mosaicTop) * (row + 1) / MosaicRows);
            for (int column = 0; column < MosaicColumns; column++)
            {
                boxes.Add((MosaicColor(frame, row, column), new Box(width * column / MosaicColumns, top, width * (column + 1) / MosaicColumns, bottom)));
            }
        }
    }

    /// <summary>Which cell of the stepping row is lit on this frame.</summary>
    public static int TickCell(long frame) => (int)(frame % TickCells);

    /// <summary>The left edge of the sweep: it starts at the left edge on each whole second.</summary>
    public static int SweepLeft(int width, long elapsedMilliseconds) => (int)(width * (elapsedMilliseconds % 1000) / 1000);

    public static PatternColor MosaicColor(long frame, int row, int column)
    {
        // A cheap integer hash; all that matters is that neighbours and successive frames differ.
        ulong x = ((ulong)frame * 0x9E3779B97F4A7C15UL) ^ ((ulong)((row * MosaicColumns) + column) * 0xC2B2AE3D27D4EB4FUL);
        x ^= x >> 29;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 32;
        return MosaicPalette[(int)(x % (ulong)MosaicPalette.Length)];
    }

    private static void AddNumber(List<(PatternColor Color, Box Box)> boxes, PatternColor color, long value, int digits, int width, int top, int height)
    {
        int digitWidth = height / 2;
        int gap = digitWidth / 3;
        int total = (digits * digitWidth) + ((digits - 1) * gap);
        int left = (width - total) / 2;

        long remaining = Math.Abs(value);
        for (int position = digits - 1; position >= 0; position--)
        {
            int digit = (int)(remaining % 10);
            remaining /= 10;
            SevenSegment.AddDigit(boxes, color, digit, left + (position * (digitWidth + gap)), top, digitWidth, height);
        }
    }
}

/// <summary>Digits made of seven bars, as on a calculator: readable in a blurred photograph.</summary>
internal static class SevenSegment
{
    /// <summary>
    /// Which segments each digit lights. Bit 0 is the top bar, then clockwise: top right, bottom
    /// right, bottom, bottom left, top left; bit 6 is the middle bar.
    /// </summary>
    private static readonly byte[] Segments =
    [
        0b0111111, // 0
        0b0000110, // 1
        0b1011011, // 2
        0b1001111, // 3
        0b1100110, // 4
        0b1101101, // 5
        0b1111101, // 6
        0b0000111, // 7
        0b1111111, // 8
        0b1101111, // 9
    ];

    public static byte SegmentsOf(int digit) => Segments[digit];

    public static void AddDigit(List<(PatternColor Color, Box Box)> boxes, PatternColor color, int digit, int left, int top, int width, int height)
    {
        int thickness = Math.Max(2, height / 8);
        int middle = top + ((height - thickness) / 2);
        int half = top + (height / 2);
        int right = left + width;
        int bottom = top + height;
        byte lit = Segments[digit];

        ReadOnlySpan<Box> bars =
        [
            new(left, top, right, top + thickness),
            new(right - thickness, top, right, half),
            new(right - thickness, half, right, bottom),
            new(left, bottom - thickness, right, bottom),
            new(left, half, left + thickness, bottom),
            new(left, top, left + thickness, half),
            new(left, middle, right, middle + thickness),
        ];

        for (int segment = 0; segment < bars.Length; segment++)
        {
            if ((lit & (1 << segment)) != 0)
            {
                boxes.Add((color, bars[segment]));
            }
        }
    }
}
