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
    /// end; the Connect button is enabled from this. USB Mirror was enabled by D65, after the
    /// production path streamed, recovered and soaked on the reference tablet (architecture
    /// sections 19 and 20); what is still to be accepted on hardware is listed in ISSUES.md.
    /// </summary>
    public static SessionCapabilities CurrentBuild { get; } = new(
        Usb: Availability.Available,
        // D74–D80 (Phase 10): complete in code and tested over loopback TLS; hardware acceptance is
        // W1–W8 in docs/quality/beta-acceptance.md.
        Wireless: Availability.Available,
        Mirror: Availability.Available,
        // D87-D89: Extend streams a virtual monitor of the user-installed Virtual Display Driver. Whether
        // one is ready is decided at run time from the display configuration (VirtualDisplayDetector).
        Extend: Availability.Available);

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
