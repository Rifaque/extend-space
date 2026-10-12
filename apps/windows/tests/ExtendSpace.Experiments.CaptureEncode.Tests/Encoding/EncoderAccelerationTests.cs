using ExtendSpace.Experiments.CaptureEncode.Encoding;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Encoding;

public class EncoderAccelerationTests
{
    private static readonly EncoderEvidence HardwareOnGpuTextures = new()
    {
        Name = "Vendor H.264 Encoder MFT",
        Clsid = new Guid("11111111-2222-3333-4444-555555555555"),
        EnumeratedAsHardware = true,
        HardwareUrl = "device-url",
        HardwareVendorId = "VEN_0000",
        IsAsync = true,
        IsD3D11Aware = true,
        AcceptedD3DManager = true,
    };

    private static readonly EncoderEvidence MicrosoftSoftware = new()
    {
        Name = "H264 Encoder MFT",
        Clsid = EncoderAcceleration.MicrosoftSoftwareH264Encoder,
        EnumeratedAsHardware = false,
    };

    [Fact]
    public void A_hardware_transform_that_encodes_from_gpu_textures_is_hardware()
    {
        AccelerationVerdict verdict = EncoderAcceleration.Classify(HardwareOnGpuTextures);

        Assert.Equal(AccelerationStatus.Hardware, verdict.Status);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void The_microsoft_software_encoder_is_software()
    {
        Assert.Equal(AccelerationStatus.Software, EncoderAcceleration.Classify(MicrosoftSoftware).Status);
    }

    [Fact]
    public void A_hardware_listing_without_a_device_behind_it_is_unknown()
    {
        Assert.Equal(
            AccelerationStatus.Unknown,
            EncoderAcceleration.Classify(HardwareOnGpuTextures with { HardwareUrl = null }).Status);
        Assert.Equal(
            AccelerationStatus.Unknown,
            EncoderAcceleration.Classify(HardwareOnGpuTextures with { HardwareUrl = string.Empty }).Status);
    }

    [Fact]
    public void A_hardware_transform_fed_through_system_memory_is_unknown()
    {
        AccelerationVerdict verdict = EncoderAcceleration.Classify(HardwareOnGpuTextures with { AcceptedD3DManager = false });

        Assert.Equal(AccelerationStatus.Unknown, verdict.Status);
    }

    [Fact]
    public void An_unrecognised_software_transform_is_unknown_not_assumed_software()
    {
        AccelerationVerdict verdict = EncoderAcceleration.Classify(
            MicrosoftSoftware with { Name = "Third-party encoder", Clsid = Guid.NewGuid() });

        Assert.Equal(AccelerationStatus.Unknown, verdict.Status);
    }

    [Fact]
    public void Being_asynchronous_or_direct3d_aware_alone_proves_nothing()
    {
        AccelerationVerdict verdict = EncoderAcceleration.Classify(HardwareOnGpuTextures with
        {
            EnumeratedAsHardware = false,
            HardwareUrl = null,
        });

        Assert.Equal(AccelerationStatus.Unknown, verdict.Status);
    }

    [Fact]
    public void Every_verdict_explains_itself()
    {
        EncoderEvidence[] cases =
        [
            HardwareOnGpuTextures,
            MicrosoftSoftware,
            HardwareOnGpuTextures with { HardwareUrl = null },
            HardwareOnGpuTextures with { AcceptedD3DManager = false },
            MicrosoftSoftware with { Clsid = Guid.Empty },
        ];

        Assert.All(cases, evidence => Assert.False(string.IsNullOrWhiteSpace(EncoderAcceleration.Classify(evidence).Reason)));
    }
}
