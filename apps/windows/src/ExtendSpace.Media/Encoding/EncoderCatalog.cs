using System.Runtime.InteropServices;
using ExtendSpace.Media.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Media.Encoding;

/// <summary>An H.264 encoder transform Media Foundation knows about, not yet activated.</summary>
internal sealed class EncoderCandidate : IDisposable
{
    private IMFActivate? _activate;

    public EncoderCandidate(IMFActivate activate, bool enumeratedAsHardware)
    {
        _activate = activate;
        EnumeratedAsHardware = enumeratedAsHardware;

        var attributes = (IMFAttributes)activate;
        Name = ReadString(attributes, PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? "(unnamed transform)";
        HardwareUrl = ReadString(attributes, PInvoke.MFT_ENUM_HARDWARE_URL_Attribute);
        HardwareVendorId = ReadString(attributes, PInvoke.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);

        try
        {
            attributes.GetGUID(PInvoke.MFT_TRANSFORM_CLSID_Attribute, out Guid clsid);
            Clsid = clsid;
        }
        catch (COMException)
        {
            // Left as Guid.Empty: the transform carries no CLSID, which the report shows as such.
        }
    }

    public string Name { get; }

    public Guid Clsid { get; }

    public bool EnumeratedAsHardware { get; }

    public string? HardwareUrl { get; }

    public string? HardwareVendorId { get; }

    public IMFActivate Activate => _activate ?? throw new ObjectDisposedException(nameof(EncoderCandidate));

    public string Describe() =>
        EnumeratedAsHardware
            ? $"{Name} [hardware transform, CLSID {Clsid:B}, vendor {HardwareVendorId ?? "?"}, device {HardwareUrl ?? "?"}]"
            : $"{Name} [software transform, CLSID {Clsid:B}]";

    public void Dispose() => ComReference.Release(ref _activate);

    private static unsafe string? ReadString(IMFAttributes attributes, in Guid key)
    {
        HRESULT result = attributes.GetAllocatedString(in key, out PWSTR value, out _);
        if (result.Failed)
        {
            return null;
        }

        try
        {
            return value.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(value.Value);
        }
    }
}

internal static class EncoderCatalog
{
    private static readonly MFT_REGISTER_TYPE_INFO H264Output = new()
    {
        guidMajorType = PInvoke.MFMediaType_Video,
        guidSubtype = PInvoke.MFVideoFormat_H264,
    };

    /// <summary>
    /// Hardware H.264 encoders, in Media Foundation's order of preference. With an adapter, only
    /// encoders that run on that adapter, because the encoder must share the capture device.
    /// </summary>
    public static unsafe List<EncoderCandidate> FindHardware(LUID? adapter)
    {
        IMFAttributes? filter = null;
        try
        {
            if (adapter is { } luid)
            {
                PInvoke.MFCreateAttributes(out filter, 1).ThrowOnFailure();
                byte[] blob = MemoryMarshal.AsBytes(new ReadOnlySpan<LUID>(in luid)).ToArray();
                filter.SetBlob(PInvoke.MFT_ENUM_ADAPTER_LUID, blob);
            }

            PInvoke.MFTEnum2(
                PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
                null,
                H264Output,
                filter,
                out IMFActivate_unmanaged** activates,
                out uint count).ThrowOnFailure();

            return Wrap(activates, count, enumeratedAsHardware: true);
        }
        finally
        {
            ComReference.Release(ref filter);
        }
    }

    /// <summary>Software H.264 encoders: everything Media Foundation lists that is not a hardware transform.</summary>
    public static unsafe List<EncoderCandidate> FindSoftware()
    {
        PInvoke.MFTEnumEx(
            PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
            null,
            H264Output,
            out IMFActivate_unmanaged** activates,
            out uint count).ThrowOnFailure();

        return Wrap(activates, count, enumeratedAsHardware: false);
    }

    private static unsafe List<EncoderCandidate> Wrap(IMFActivate_unmanaged** activates, uint count, bool enumeratedAsHardware)
    {
        var candidates = new List<EncoderCandidate>((int)count);
        try
        {
            for (uint i = 0; i < count; i++)
            {
                nint pointer = (nint)activates[i];
                try
                {
                    candidates.Add(new EncoderCandidate((IMFActivate)Marshal.GetObjectForIUnknown(pointer), enumeratedAsHardware));
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }
        finally
        {
            PInvoke.CoTaskMemFree(activates);
        }

        return candidates;
    }
}
