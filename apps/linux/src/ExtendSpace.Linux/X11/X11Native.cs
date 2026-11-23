using System.Runtime.InteropServices;

namespace ExtendSpace.Linux.X11;

/// <summary>Xlib, XRandR 1.5 monitors and XTest, as the X11 backend uses them (D99, D100).</summary>
internal static unsafe partial class X11Native
{
    private const string X11 = "libX11.so.6";
    private const string Xrandr = "libXrandr.so.2";
    private const string Xtst = "libXtst.so.6";

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public nuint Name;
        public int Primary;
        public int Automatic;
        public int NOutput;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MWidth;
        public int MHeight;
        public nint Outputs;
    }

    [LibraryImport(X11, EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint OpenDisplay(string? name);

    [LibraryImport(X11, EntryPoint = "XCloseDisplay")]
    public static partial int CloseDisplay(nint display);

    [LibraryImport(X11, EntryPoint = "XDefaultRootWindow")]
    public static partial nuint DefaultRootWindow(nint display);

    [LibraryImport(X11, EntryPoint = "XGetAtomName")]
    public static partial nint GetAtomName(nint display, nuint atom);

    [LibraryImport(X11, EntryPoint = "XFree")]
    public static partial int Free(nint data);

    [LibraryImport(X11, EntryPoint = "XFlush")]
    public static partial int Flush(nint display);

    [LibraryImport(X11, EntryPoint = "XSync")]
    public static partial int Sync(nint display, int discard);

    [LibraryImport(Xrandr, EntryPoint = "XRRGetMonitors")]
    public static partial MonitorInfo* GetMonitors(nint display, nuint window, int getActive, out int count);

    [LibraryImport(Xrandr, EntryPoint = "XRRFreeMonitors")]
    public static partial void FreeMonitors(MonitorInfo* monitors);

    [LibraryImport(Xtst, EntryPoint = "XTestQueryExtension")]
    public static partial int TestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeMotionEvent")]
    public static partial int TestFakeMotion(nint display, int screen, int x, int y, nuint delay);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeButtonEvent")]
    public static partial int TestFakeButton(nint display, uint button, int isPress, nuint delay);
}
