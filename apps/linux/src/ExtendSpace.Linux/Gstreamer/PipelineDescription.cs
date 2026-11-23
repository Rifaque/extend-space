using System.Globalization;
using ExtendSpace.Core.Input;

namespace ExtendSpace.Linux.Gstreamer;

/// <summary>Where the pictures come from (D97).</summary>
public abstract record CaptureSource
{
    /// <summary>An X11 screen region (a RandR monitor or the virtual area, D99), read with <c>ximagesrc</c>.</summary>
    public sealed record X11Region(string Display, ScreenRect Region) : CaptureSource;

    /// <summary>
    /// A PipeWire stream the ScreenCast portal opened (D98): its remote's file descriptor and node. For a
    /// virtual monitor the requested size is offered first in negotiation, which is how GNOME sizes it.
    /// </summary>
    public sealed record PipeWireNode(int FileDescriptor, uint NodeId, int? Width = null, int? Height = null) : CaptureSource;
}

/// <summary>An H.264 encoder element and whether it runs on the GPU.</summary>
public sealed record EncoderChoice(string Element, bool IsHardware, string Name);

/// <summary>
/// Builds the <c>gst_parse_launch</c> text of the capture-and-encode pipeline (D97). Pure, so its
/// exact output is tested; GStreamer itself is only needed to run it.
/// </summary>
public static class PipelineDescription
{
    public const string SinkName = "es_sink";
    public const string EncoderName = "es_encoder";

    /// <summary>
    /// The encoders by measured latency (ES-165, CachyOS 2026-11-23, real PipeWire capture at 1080p60):
    /// NVENC holds a frame about 3 ms; x264 (zerolatency, ultrafast) about 1 ms at more CPU; the VA-API
    /// encoders about 67 ms (four frames) when fed from PipeWire, which users feel as lag. Only the ones
    /// the system has are used; <see cref="EncoderVariable"/> picks one explicitly (for example VA-API
    /// to save power).
    /// </summary>
    public static readonly IReadOnlyList<EncoderChoice> Encoders =
    [
        new("nvh264enc", true, "NVENC H.264"),
        new("x264enc", false, "x264 (software)"),
        new("vah264enc", true, "VA-API H.264 (vah264enc)"),
        new("vaapih264enc", true, "VA-API H.264 (vaapih264enc)"),
    ];

    /// <summary>Names one encoder element to use instead of the automatic order (troubleshooting, measurements).</summary>
    public const string EncoderVariable = "EXTENDSPACE_ENCODER";

    /// <param name="forced">An element name from <see cref="EncoderVariable"/>; only that encoder is then considered.</param>
    public static EncoderChoice? ChooseEncoder(Func<string, bool> available, bool allowHardware = true, string? forced = null) =>
        Encoders.FirstOrDefault(e => (allowHardware || !e.IsHardware) && available(e.Element) && (string.IsNullOrEmpty(forced) || e.Element == forced));

    /// <param name="parser">Whether <c>h264parse</c> (gst-plugins-bad) is installed. Without it the encoder's own
    /// byte stream is used: x264 repeats SPS and PPS before every IDR by default.</param>
    public static string Build(CaptureSource source, int width, int height, int frameRate, int bitrate, EncoderChoice encoder, bool parser = true)
    {
        string fps = frameRate.ToString(CultureInfo.InvariantCulture);
        string capture = source switch
        {
            CaptureSource.X11Region x => string.Create(CultureInfo.InvariantCulture,
                $"ximagesrc display-name={x.Display} startx={x.Region.Left} starty={x.Region.Top} endx={x.Region.Left + x.Region.Width - 1} endy={x.Region.Top + x.Region.Height - 1} use-damage=false show-pointer=true"),
            // keepalive-time repeats the last picture while the screen is still, so a keyframe can always be made.
            CaptureSource.PipeWireNode p => string.Create(CultureInfo.InvariantCulture,
                $"pipewiresrc fd={p.FileDescriptor} path={p.NodeId} do-timestamp=true keepalive-time=500 always-copy=true{(p.Width is { } w && p.Height is { } h ? $" ! video/x-raw,width={w},height={h}" : string.Empty)}"),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        string format = encoder.Element == "x264enc" ? "I420" : "NV12";
        int kbps = Math.Max(500, bitrate / 1000);
        int gop = frameRate * 2;
        string encode = encoder.Element switch
        {
            "x264enc" => $"x264enc name={EncoderName} tune=zerolatency speed-preset=ultrafast bitrate={kbps} key-int-max={gop} bframes=0 byte-stream=true",
            "vah264enc" => $"vah264enc name={EncoderName} bitrate={kbps} key-int-max={gop} b-frames=0 rate-control=cbr target-usage=7",
            "vaapih264enc" => $"vaapih264enc name={EncoderName} bitrate={kbps} keyframe-period={gop} max-bframes=0 rate-control=cbr tune=low-power",
            "nvh264enc" => $"nvh264enc name={EncoderName} bitrate={kbps} gop-size={gop} bframes=0 rc-mode=cbr zerolatency=true preset=low-latency-hq",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder)),
        };
        string profile = encoder.Element == "x264enc" ? ",profile=constrained-baseline" : string.Empty;

        return string.Create(CultureInfo.InvariantCulture,
            // videorate only drops (ES-166): duplicating made it hold each picture until the next arrived,
            // up to the 500 ms keepalive on a still screen, so the last change of a movement came late.
            $"{capture} ! videorate drop-only=true max-rate={fps} ! videoconvert ! videoscale add-borders=true ! video/x-raw,format={format},width={width},height={height},pixel-aspect-ratio=1/1 ! queue max-size-buffers=2 leaky=downstream ! {encode} ! {(parser ? "h264parse config-interval=-1 ! " : string.Empty)}video/x-h264,stream-format=byte-stream,alignment=au{profile} ! appsink name={SinkName} sync=false max-buffers=4 drop=false emit-signals=false");
    }
}

/// <summary>Annex-B helpers for what the encoder produced.</summary>
public static class AnnexB
{
    /// <summary>True when the access unit holds an IDR slice (NAL type 5).</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> accessUnit)
    {
        for (int i = 0; i + 3 < accessUnit.Length; i++)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0 && (accessUnit[i + 2] == 1 || (accessUnit[i + 2] == 0 && i + 4 < accessUnit.Length && accessUnit[i + 3] == 1)))
            {
                int header = accessUnit[i + 2] == 1 ? i + 3 : i + 4;
                if (header < accessUnit.Length && (accessUnit[header] & 0x1F) == 5)
                {
                    return true;
                }

                i = header - 1;
            }
        }

        return false;
    }
}
