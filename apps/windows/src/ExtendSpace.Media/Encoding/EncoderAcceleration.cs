namespace ExtendSpace.Media.Encoding;

internal enum AccelerationStatus
{
    /// <summary>Positively identified as a hardware encoder.</summary>
    Hardware,

    /// <summary>Positively identified as a software encoder.</summary>
    Software,

    /// <summary>The available evidence does not settle it either way.</summary>
    Unknown,
}

/// <summary>What Media Foundation and the transform itself reported about the encoder that ran.</summary>
internal sealed record EncoderEvidence
{
    public required string Name { get; init; }

    public required Guid Clsid { get; init; }

    /// <summary>Returned by an enumeration restricted to <c>MFT_ENUM_FLAG_HARDWARE</c>.</summary>
    public required bool EnumeratedAsHardware { get; init; }

    /// <summary><c>MFT_ENUM_HARDWARE_URL_Attribute</c>: the device the transform is a proxy for.</summary>
    public string? HardwareUrl { get; init; }

    /// <summary><c>MFT_ENUM_HARDWARE_VENDOR_ID_Attribute</c>.</summary>
    public string? HardwareVendorId { get; init; }

    /// <summary><c>MF_TRANSFORM_ASYNC</c> on the activated transform.</summary>
    public bool IsAsync { get; init; }

    /// <summary><c>MF_SA_D3D11_AWARE</c> on the activated transform.</summary>
    public bool IsD3D11Aware { get; init; }

    /// <summary><c>MFT_MESSAGE_SET_D3D_MANAGER</c> succeeded with the capture device's manager.</summary>
    public bool AcceptedD3DManager { get; init; }
}

internal sealed record AccelerationVerdict(AccelerationStatus Status, string Reason);

internal static class EncoderAcceleration
{
    /// <summary>CLSID_MSH264EncoderMFT, Microsoft's software "H264 Encoder MFT" (mfh264enc.dll).</summary>
    public static readonly Guid MicrosoftSoftwareH264Encoder = new("6ca50344-051a-4ded-9779-a43305165e35");

    /// <summary>
    /// Classifies only on positive evidence. Anything short of it is <see cref="AccelerationStatus.Unknown"/>,
    /// never an assumption from the machine having a GPU.
    /// </summary>
    public static AccelerationVerdict Classify(EncoderEvidence evidence)
    {
        if (evidence.Clsid == MicrosoftSoftwareH264Encoder && !evidence.EnumeratedAsHardware)
        {
            return new(
                AccelerationStatus.Software,
                "The transform is Microsoft's software H.264 encoder (CLSID_MSH264EncoderMFT), which Media "
                + "Foundation does not list as a hardware transform.");
        }

        if (evidence.EnumeratedAsHardware)
        {
            if (string.IsNullOrEmpty(evidence.HardwareUrl))
            {
                return new(
                    AccelerationStatus.Unknown,
                    "Media Foundation listed the transform as hardware, but it carries no hardware device URL, "
                    + "so the device behind it could not be identified.");
            }

            if (!evidence.AcceptedD3DManager)
            {
                return new(
                    AccelerationStatus.Unknown,
                    "Media Foundation listed the transform as hardware, but it did not take the Direct3D 11 "
                    + "device manager, so frames reached it through system memory and its use of the GPU is unconfirmed.");
            }

            return new(
                AccelerationStatus.Hardware,
                "Media Foundation listed the transform under MFT_ENUM_FLAG_HARDWARE as a proxy for a hardware "
                + "device, and it accepted the Direct3D 11 device manager and encoded from GPU textures.");
        }

        return new(
            AccelerationStatus.Unknown,
            "The transform is neither listed by Media Foundation as hardware nor the known Microsoft software encoder.");
    }
}
