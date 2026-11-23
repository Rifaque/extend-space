using System.Runtime.InteropServices;

namespace ExtendSpace.Linux.Gstreamer;

/// <summary>
/// The few GStreamer C functions the pipeline uses (D97), from the distribution's own libraries
/// (<c>libgstreamer-1.0.so.0</c>, <c>libgstapp-1.0.so.0</c>, <c>libgstvideo-1.0.so.0</c>). Nothing is
/// bundled; GStreamer's ABI has been stable across the 1.x series.
/// </summary>
internal static unsafe partial class GstNative
{
    private const string Gst = "libgstreamer-1.0.so.0";
    private const string GstApp = "libgstapp-1.0.so.0";
    private const string GstVideo = "libgstvideo-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    public const int StateNull = 1;
    public const int StatePlaying = 4;
    public const int StateChangeFailure = 0;
    public const uint MessageEos = 1 << 0;
    public const uint MessageError = 1 << 1;
    public const int MapRead = 1;
    public const ulong ClockTimeNone = ulong.MaxValue;
    public const ulong BufferFlagDeltaUnit = 1 << 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct MapInfo
    {
        public nint Memory;
        public int Flags;
        public byte* Data;
        public nuint Size;
        public nuint MaxSize;
        public fixed byte UserData[4 * 8];
        public fixed byte Padding[4 * 8];
    }

    [LibraryImport(Gst, EntryPoint = "gst_init_check")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCheck(nint argc, nint argv, out nint error);

    [LibraryImport(Gst, EntryPoint = "gst_parse_launch", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ParseLaunch(string description, out nint error);

    [LibraryImport(Gst, EntryPoint = "gst_element_set_state")]
    public static partial int SetState(nint element, int state);

    [LibraryImport(Gst, EntryPoint = "gst_element_get_state")]
    public static partial int GetState(nint element, out int state, out int pending, ulong timeout);

    [LibraryImport(Gst, EntryPoint = "gst_bin_get_by_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint BinGetByName(nint bin, string name);

    [LibraryImport(Gst, EntryPoint = "gst_element_get_bus")]
    public static partial nint GetBus(nint element);

    [LibraryImport(Gst, EntryPoint = "gst_bus_timed_pop_filtered")]
    public static partial nint BusPop(nint bus, ulong timeout, uint types);

    [LibraryImport(Gst, EntryPoint = "gst_message_parse_error")]
    public static partial void MessageParseError(nint message, out nint error, out nint debug);

    [LibraryImport(Gst, EntryPoint = "gst_mini_object_unref")]
    public static partial void MiniObjectUnref(nint obj);

    [LibraryImport(Gst, EntryPoint = "gst_object_unref")]
    public static partial void ObjectUnref(nint obj);

    [LibraryImport(Gst, EntryPoint = "gst_element_factory_find", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FactoryFind(string name);

    [LibraryImport(Gst, EntryPoint = "gst_element_send_event")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SendEvent(nint element, nint gstEvent);

    [LibraryImport(Gst, EntryPoint = "gst_sample_get_buffer")]
    public static partial nint SampleGetBuffer(nint sample);

    [LibraryImport(Gst, EntryPoint = "gst_buffer_map")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BufferMap(nint buffer, out MapInfo info, int flags);

    [LibraryImport(Gst, EntryPoint = "gst_buffer_unmap")]
    public static partial void BufferUnmap(nint buffer, ref MapInfo info);

    [LibraryImport(GstApp, EntryPoint = "gst_app_sink_try_pull_sample")]
    public static partial nint AppSinkTryPull(nint appsink, ulong timeout);

    [LibraryImport(GstVideo, EntryPoint = "gst_video_event_new_upstream_force_key_unit")]
    public static partial nint NewUpstreamForceKeyUnit(ulong runningTime, [MarshalAs(UnmanagedType.Bool)] bool allHeaders, uint count);

    [LibraryImport(GLib, EntryPoint = "g_error_free")]
    public static partial void ErrorFree(nint error);

    [LibraryImport(GLib, EntryPoint = "g_free")]
    public static partial void Free(nint memory);

    [LibraryImport(GObject, EntryPoint = "g_object_unref")]
    public static partial void GObjectUnref(nint obj);

    /// <summary>The message of a <c>GError*</c> (its <c>message</c> field follows domain and code).</summary>
    public static string ErrorMessage(nint error) =>
        error == 0 ? "unknown error" : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";

    private static readonly Lazy<string?> Initialised = new(() =>
    {
        try
        {
            if (!InitCheck(0, 0, out nint error))
            {
                string message = ErrorMessage(error);
                ErrorFree(error);
                return message;
            }

            return null;
        }
        catch (DllNotFoundException)
        {
            return "GStreamer is not installed (libgstreamer-1.0.so.0 was not found).";
        }
    });

    /// <summary>Initialises GStreamer once; returns why it is unusable, or null.</summary>
    public static string? EnsureInitialised() => Initialised.Value;

    public static bool HasElement(string name)
    {
        nint factory = FactoryFind(name);
        if (factory == 0)
        {
            return false;
        }

        ObjectUnref(factory);
        return true;
    }
}
