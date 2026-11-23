using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Media.Encoding;

/// <summary>Keeps Media Foundation started for as long as it is alive.</summary>
internal sealed class MediaFoundationSession : IDisposable
{
    private bool _started;

    public MediaFoundationSession()
    {
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
        _started = true;
    }

    public void Dispose()
    {
        if (_started)
        {
            _started = false;
            PInvoke.MFShutdown();
        }
    }
}

internal static class MediaTypes
{
    public static IMFMediaType CreateH264Output(EncoderSettings settings)
    {
        IMFMediaType type = CreateVideoType(PInvoke.MFVideoFormat_H264, settings);
        type.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, (uint)settings.BitrateBitsPerSecond);
        type.SetUINT32(PInvoke.MF_MT_MPEG2_PROFILE, (uint)ToMediaFoundationProfile(settings.Profile));
        return type;
    }

    public static IMFMediaType CreateNv12Input(EncoderSettings settings)
    {
        IMFMediaType type = CreateVideoType(PInvoke.MFVideoFormat_NV12, settings);

        // Matches what Nv12Converter produces. Whether this reaches the stream's VUI is up to the encoder.
        type.SetUINT32(PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
        type.SetUINT32(PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
        type.SetUINT32(PInvoke.MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
        type.SetUINT32(PInvoke.MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
        return type;
    }

    public static eAVEncH264VProfile ToMediaFoundationProfile(H264Profile profile) => profile switch
    {
        H264Profile.Baseline => eAVEncH264VProfile.eAVEncH264VProfile_Base,
        H264Profile.Main => eAVEncH264VProfile.eAVEncH264VProfile_Main,
        H264Profile.High => eAVEncH264VProfile.eAVEncH264VProfile_High,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Media Foundation stores sizes and ratios as two 32-bit values in one 64-bit attribute.</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static IMFMediaType CreateVideoType(Guid subtype, EncoderSettings settings)
    {
        PInvoke.MFCreateMediaType(out IMFMediaType type).ThrowOnFailure();
        type.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        type.SetGUID(PInvoke.MF_MT_SUBTYPE, subtype);
        type.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, Pack((uint)settings.Width, (uint)settings.Height));
        type.SetUINT64(PInvoke.MF_MT_FRAME_RATE, Pack((uint)settings.FrameRate, 1));
        type.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        type.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        return type;
    }
}

/// <summary>The fixed parameters an encoder is opened with.</summary>
internal sealed record EncoderSettings(
    int Width,
    int Height,
    int FrameRate,
    int BitrateBitsPerSecond,
    int KeyframeIntervalFrames,
    H264Profile Profile)
{
    /// <summary>Nominal duration of one frame, in the 100 ns units Media Foundation uses.</summary>
    public long FrameDurationTicks => TimeSpan.TicksPerSecond / FrameRate;
}

internal sealed class EncoderException : Exception
{
    public EncoderException(string message)
        : base(message)
    {
    }

    public EncoderException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public static EncoderException FromResult(string operation, HRESULT result) =>
        new($"{operation} failed with 0x{result.Value:X8}.");
}
