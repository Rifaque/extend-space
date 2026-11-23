using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Display;

/// <summary>A display mode: pixel size and refresh rate. The same model on every host platform (D92).</summary>
public sealed record DisplayModeSpec(int Width, int Height, int RefreshHz)
{
    public long Pixels => (long)Width * Height;

    public double Aspect => (double)Width / Height;

    public override string ToString() => $"{Width} × {Height} at {RefreshHz} Hz";
}

/// <summary>The screen of the tablet the advice is for, as its <c>capabilities</c> message reported it.</summary>
public sealed record TabletScreen(int Width, int Height, double RefreshHz)
{
    /// <summary>The landscape shape; tablets report the orientation they were in.</summary>
    public TabletScreen Landscape => Width >= Height ? this : new TabletScreen(Height, Width, RefreshHz);
}

/// <summary>A mode with the figures it was judged on.</summary>
/// <param name="Coverage">How much of the tablet's screen the picture fills (letterboxing lowers it).</param>
/// <param name="Sharpness">1 when streaming sends every pixel; less when the mode is scaled down to the stream envelope.</param>
/// <param name="Room">How much desktop the mode gives, against the stream envelope (square root, capped at 1).</param>
public sealed record ModeScore(DisplayModeSpec Mode, double Coverage, double Sharpness, double Room)
{
    public double Value => Coverage * Sharpness * Room;
}

/// <summary>What ExtendSpace suggests for the virtual display.</summary>
public sealed record ModeAdvice(
    DisplayModeSpec? Current,
    ModeScore? Recommended,
    bool CurrentIsRecommended,
    DisplayModeSpec? CustomSuggestion,
    string Message);

/// <summary>
/// Chooses the virtual display's mode for a tablet (D92). Generic: any tablet shape, any list of modes
/// a driver offers; nothing in it names a device. Streaming fits every picture into the D89 envelope
/// (<see cref="StreamConfigurationPolicy.Preferred"/>), so a mode larger than the envelope is sent
/// scaled down, which blurs text; a mode of another shape than the tablet is letterboxed.
/// </summary>
public static class TabletModeAdvisor
{
    /// <summary>Streams run at up to 60 frames per second; a faster display only costs capture work.</summary>
    public const int TargetRefreshHz = 60;

    private const double SignificantGain = 0.03;

    public static ModeScore Score(DisplayModeSpec mode, TabletScreen? tablet)
    {
        EncodingRequest envelope = StreamConfigurationPolicy.Preferred;
        bool portrait = mode.Height > mode.Width;
        int boxWidth = portrait ? envelope.Height : envelope.Width;
        int boxHeight = portrait ? envelope.Width : envelope.Height;
        double down = Math.Min(1.0, Math.Min((double)boxWidth / mode.Width, (double)boxHeight / mode.Height));
        double sharpness = down;
        double room = Math.Min(1.0, Math.Sqrt(mode.Pixels / ((double)boxWidth * boxHeight)));
        double coverage = 1.0;
        if (tablet is not null)
        {
            TabletScreen screen = portrait ? new TabletScreen(tablet.Landscape.Height, tablet.Landscape.Width, tablet.RefreshHz) : tablet.Landscape;
            double tabletAspect = (double)screen.Width / screen.Height;
            coverage = mode.Aspect >= tabletAspect ? tabletAspect / mode.Aspect : mode.Aspect / tabletAspect;
        }

        return new ModeScore(mode, coverage, sharpness, room);
    }

    /// <summary>
    /// The best of <paramref name="available"/> for <paramref name="tablet"/> (null when not known yet:
    /// then shape is not judged). Refresh: 60 Hz when offered, otherwise the nearest above 30.
    /// </summary>
    public static ModeAdvice Advise(IReadOnlyList<DisplayModeSpec> available, DisplayModeSpec? current, TabletScreen? tablet)
    {
        DisplayModeSpec? custom = tablet is null ? null : CustomSuggestion(tablet);
        ModeScore? best = available
            .GroupBy(mode => (mode.Width, mode.Height))
            .Select(sizes => sizes.OrderBy(mode => RefreshCost(mode.RefreshHz)).First())
            .Select(mode => Score(mode, tablet))
            .OrderByDescending(score => score.Value)
            .ThenByDescending(score => score.Mode.Pixels)
            .FirstOrDefault();
        if (best is null)
        {
            return new ModeAdvice(current, null, false, custom, "The display lists no modes.");
        }

        ModeScore? now = current is null ? null : Score(current, tablet);
        bool currentGood = now is not null
            && now.Value >= best.Value - SignificantGain
            && RefreshCost(current!.RefreshHz) <= RefreshCost(best.Mode.RefreshHz);
        string message = currentGood
            ? $"{current} suits this tablet."
            : $"{best.Mode} would suit this tablet better than {current?.ToString() ?? "the current mode"}.";
        if (custom is not null && best.Coverage < 0.97)
        {
            message += $" The driver has no mode of the tablet's shape; adding {custom.Width} × {custom.Height} in the driver's own settings would fill the screen.";
        }

        return new ModeAdvice(current, best, currentGood, custom, message);
    }

    /// <summary>
    /// The largest size of the tablet's landscape shape that fits the stream envelope without scaling,
    /// on even numbers (1620 × 1080 for an exact 3:2 screen; 1616 × 1080 for 3200 × 2136). Larger 3:2 sizes such as 1920 × 1280 or
    /// 2400 × 1600 give more desktop but are scaled down to this for streaming (D89, D92).
    /// </summary>
    public static DisplayModeSpec CustomSuggestion(TabletScreen tablet)
    {
        TabletScreen screen = tablet.Landscape;
        EncodingRequest envelope = StreamConfigurationPolicy.Preferred;
        long width = envelope.Width;
        long height = (width * screen.Height) / screen.Width;
        if (height > envelope.Height)
        {
            height = envelope.Height;
            width = (height * screen.Width) / screen.Height;
        }

        return new DisplayModeSpec((int)(width & ~1L), (int)(height & ~1L), TargetRefreshHz);
    }

    private static int RefreshCost(int hz) => hz == TargetRefreshHz ? 0 : hz > TargetRefreshHz ? hz - TargetRefreshHz : 1000 + (TargetRefreshHz - hz);
}

/// <summary>A host's access to its virtual display's modes (D92). Applying changes only that display.</summary>
public interface IVirtualDisplayModes
{
    IReadOnlyList<DisplayModeSpec> Available(string deviceName);

    DisplayModeSpec? Current(string deviceName);

    /// <summary>Changes the mode of <paramref name="deviceName"/>, which must be a virtual display; returns a problem or null.</summary>
    string? Apply(string deviceName, DisplayModeSpec mode);
}

/// <summary>
/// Where a window that is on the virtual display goes when the user brings windows back (ES-141): the
/// same offset on the destination display, kept inside it. Null when the window's centre is not on the
/// virtual display. Platform-neutral: Windows and Linux X11 hosts use it alike.
/// </summary>
public static class WindowRescuePlan
{
    public static Input.ScreenPoint? Destination(Input.ScreenRect window, Input.ScreenRect virtualDisplay, Input.ScreenRect destination)
    {
        int centreX = window.Left + (window.Width / 2);
        int centreY = window.Top + (window.Height / 2);
        if (centreX < virtualDisplay.Left || centreX >= virtualDisplay.Left + virtualDisplay.Width
            || centreY < virtualDisplay.Top || centreY >= virtualDisplay.Top + virtualDisplay.Height)
        {
            return null;
        }

        int x = destination.Left + (window.Left - virtualDisplay.Left);
        int y = destination.Top + (window.Top - virtualDisplay.Top);
        x = Math.Clamp(x, destination.Left, Math.Max(destination.Left, destination.Left + destination.Width - window.Width));
        y = Math.Clamp(y, destination.Top, Math.Max(destination.Top, destination.Top + destination.Height - window.Height));
        return new Input.ScreenPoint(x, y);
    }
}

/// <summary>Moves the windows on the virtual display to the main display, when the user asks (ES-141).</summary>
public interface IWindowRescue
{
    /// <summary>Returns how many windows were moved.</summary>
    int BringBack(string virtualDeviceName);
}
