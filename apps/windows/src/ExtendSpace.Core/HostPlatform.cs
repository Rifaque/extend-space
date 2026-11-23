namespace ExtendSpace.Core;

/// <summary>
/// The host operating system as the user-facing text names it (D101). Core is shared by the Windows
/// and Linux hosts; text that names the system the picture comes from asks here instead of saying
/// "Windows". Overridable for tests.
/// </summary>
public static class HostPlatform
{
    private static string? _name;

    /// <summary>"Windows", "Linux", or the OS description elsewhere.</summary>
    public static string Name
    {
        get => _name ?? (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        set => _name = value;
    }

    public static bool IsWindows => Name == "Windows";
}
