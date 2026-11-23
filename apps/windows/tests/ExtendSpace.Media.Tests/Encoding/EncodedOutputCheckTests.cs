using ExtendSpace.Media.Encoding;

namespace ExtendSpace.Media.Tests.Encoding;

/// <summary>
/// Reading what an encoder produced (its sequence parameter set) and holding it to the stream the
/// client was configured for (D57). The parameter sets are real, the same bytes and expected values
/// as the Android client's <c>SequenceParameterSetTest</c> (each copied from a file the capture
/// experiment wrote, the values as ffprobe reports them), so both ends read a stream the same way.
/// </summary>
public sealed class EncodedOutputCheckTests
{
    public static TheoryData<string, string, int, int, string, string, double> Samples => new()
    {
        { "Intel Quick Sync", "27640033ac2b60140064d80a04000003000400000301e3a10002dc600002dc6c6f7be0ed0e1970", 2560, 1600, "High", "5.1", 60.0 },
        { "Intel Quick Sync, 165 fps", "27640034ac2b60140064d80a0400000300040000052ba10002dc600002dc6c6f7be0ed0e1970", 2560, 1600, "High", "5.2", 165.0 },
        { "Microsoft software", "67640033ac2b40140064d80880000003008000003c06d0442350", 2560, 1600, "High", "5.1", 60.0 },
        { "Microsoft software", "6764002aac2b403c0113f2e022000003000200000300f01b41108d40", 1920, 1080, "High", "4.2", 60.0 },
        { "NVIDIA", "67640033ac2b4014005ad808800001f40000ea60078e1550", 2560, 1440, "High", "5.1", 60.0 },
        { "NVIDIA", "6764002aac2b403c0113f2e022000007d00003a9801e385540", 1920, 1080, "High", "4.2", 60.0 },
        { "NVIDIA", "6764002aac2b402b030f37808800001f40000ea60078e155", 1366, 768, "High", "4.2", 60.0 },
        { "NVIDIA, Main, 30 fps", "674d401f95a014016ec04400000fa00003a9803c70aa80", 1280, 720, "Main", "3.1", 30.0 },
        { "NVIDIA, Baseline", "6742c01f95a0360f79bc04400000fa0000753003c70aa8", 854, 480, "Constrained Baseline", "3.1", 60.0 },
    };

    internal const string Nvidia1080p = "6764002aac2b403c0113f2e022000007d00003a9801e385540";
    internal const string Nvidia720pMain = "674d401f95a014016ec04400000fa00003a9803c70aa80";

    [Theory]
    [MemberData(nameof(Samples))]
    public void Real_parameter_sets_read_as_ffprobe_reports_them(string encoder, string hex, int width, int height, string profile, string level, double rate)
    {
        H264SequenceParameterSet sps = H264SequenceParameterSet.Parse(Convert.FromHexString(hex));

        Assert.Equal((width, height, profile, level), (sps.Width, sps.Height, sps.ProfileName, sps.LevelName));
        Assert.Equal(rate, sps.FramesPerSecond!.Value, 3);
        Assert.False(sps.Interlaced, encoder);
    }

    [Fact]
    public void Something_that_is_not_a_parameter_set_or_is_cut_short_is_refused()
    {
        Assert.Throws<FormatException>(() => H264SequenceParameterSet.Parse(Convert.FromHexString("68ee3cb0")));
        Assert.Throws<FormatException>(() => H264SequenceParameterSet.Parse([]));
        byte[] whole = Convert.FromHexString(Nvidia1080p);
        for (int length = 1; length <= 12; length++)
        {
            Assert.Throws<FormatException>(() => H264SequenceParameterSet.Parse(whole.AsSpan(0, length)));
        }
    }

    [Fact]
    public void A_keyframe_of_the_configured_size_and_profile_passes_and_is_recorded()
    {
        var check = new EncodedOutputCheck(1920, 1080, H264Profile.High);

        check.Verify(Keyframe(Nvidia1080p));

        Assert.Equal(1, check.Checked);
        Assert.Equal("1920 x 1080, High profile, level 4.2, 60 fps signalled", check.First!.ToString());
    }

    [Fact]
    public void An_access_unit_without_a_parameter_set_is_not_checked()
    {
        var check = new EncodedOutputCheck(1920, 1080, H264Profile.High);

        check.Verify([0, 0, 0, 1, 0x41, 0x9A, 0x24]);

        Assert.Equal(0, check.Checked);
        Assert.Null(check.First);
    }

    [Theory]
    [InlineData(1920, 1080, Nvidia720pMain, "1280 x 720, Main profile")]
    [InlineData(1280, 720, Nvidia1080p, "1920 x 1080, High profile")]
    [InlineData(1280, 720, Nvidia720pMain, "1280 x 720, Main profile")]
    public void Another_size_or_profile_than_the_client_was_told_is_an_explicit_encoder_fault(int width, int height, string produced, string described)
    {
        var check = new EncodedOutputCheck(width, height, H264Profile.High);

        MediaEncoderException failure = Assert.Throws<MediaEncoderException>(() => check.Verify(Keyframe(produced)));

        Assert.Equal(MediaFaultCodes.EncoderOutputMismatch, failure.Code);
        Assert.Contains(described, failure.Message, StringComparison.Ordinal);
        Assert.Contains($"told {width} x {height}, High profile", failure.Message, StringComparison.Ordinal);
        Assert.Null(check.First);
    }

    [Fact]
    public void A_parameter_set_that_cannot_be_read_is_an_explicit_encoder_fault()
    {
        var check = new EncodedOutputCheck(1920, 1080, H264Profile.High);

        MediaEncoderException failure = Assert.Throws<MediaEncoderException>(() => check.Verify([0, 0, 0, 1, 0x67, 0x64, 0x00]));

        Assert.Equal(MediaFaultCodes.EncoderOutputMismatch, failure.Code);
    }

    /// <summary>An access unit as an encoder emits a keyframe: the SPS, a PPS and an IDR slice.</summary>
    internal static byte[] Keyframe(string spsHex) =>
        [0, 0, 0, 1, .. Convert.FromHexString(spsHex), 0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21, 0xA0];
}
