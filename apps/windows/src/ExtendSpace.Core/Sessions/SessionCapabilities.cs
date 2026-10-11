namespace ExtendSpace.Core.Sessions;

/// <param name="Reason">Why the feature cannot be used; null when it is available.</param>
public sealed record Availability(bool IsAvailable, string? Reason)
{
    public static Availability Available { get; } = new(true, null);

    public static Availability Unavailable(string reason) => new(false, reason);
}

/// <summary>Which transports and display modes this build can actually start.</summary>
public sealed record SessionCapabilities(
    Availability Usb,
    Availability Wireless,
    Availability Mirror,
    Availability Extend)
{
    /// <summary>
    /// The truth about the current build. Flip an entry only when the feature really works end to
    /// end; the Connect button is enabled from this.
    /// </summary>
    public static SessionCapabilities CurrentBuild { get; } = new(
        Usb: Availability.Unavailable("The USB transport is not implemented yet."),
        Wireless: Availability.Unavailable("Wireless pairing and transport are not implemented yet."),
        Mirror: Availability.Unavailable("Display capture and video streaming are not implemented yet."),
        Extend: Availability.Unavailable("Extending requires the ExtendSpace virtual display driver, which does not exist yet."));

    public Availability For(TransportKind transport) => transport switch
    {
        TransportKind.Usb => Usb,
        TransportKind.Wireless => Wireless,
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    public Availability For(DisplayMode mode) => mode switch
    {
        DisplayMode.Mirror => Mirror,
        DisplayMode.Extend => Extend,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
