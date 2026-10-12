using ExtendSpace.Experiments.CaptureEncode.Bitstream;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Bitstream;

public sealed class ElementaryStreamWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"extendspace-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Access_units_are_written_back_to_back_with_nothing_added()
    {
        string path = NewPath();

        using (var writer = new ElementaryStreamWriter(path))
        {
            writer.WriteAccessUnit(H264Samples.IdrAccessUnit);
            writer.WriteAccessUnit(H264Samples.NonIdrAccessUnit);

            Assert.Equal(2, writer.Statistics.AccessUnits);
            Assert.False(writer.SequenceHeaderPrepended);
            Assert.NotEqual(0, writer.LastWriteTimestamp);
        }

        Assert.Equal([.. H264Samples.IdrAccessUnit, .. H264Samples.NonIdrAccessUnit], File.ReadAllBytes(path));
    }

    [Fact]
    public void The_output_directory_is_created_and_the_path_made_absolute()
    {
        string path = Path.Combine(_directory, "nested", "deeper", "clip.h264");

        using (var writer = new ElementaryStreamWriter(path))
        {
            writer.WriteAccessUnit(H264Samples.IdrAccessUnit);
            Assert.True(Path.IsPathFullyQualified(writer.Path));
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_encoder_s_sequence_header_is_written_first_when_the_stream_does_not_start_with_one()
    {
        string path = NewPath();
        byte[] header = H264Samples.Join(H264Samples.Sps, H264Samples.Pps);
        byte[] bareIdr = H264Samples.Join(H264Samples.IdrSlice);

        using (var writer = new ElementaryStreamWriter(path) { SequenceHeaderProvider = () => header })
        {
            writer.WriteAccessUnit(bareIdr);
            writer.WriteAccessUnit(H264Samples.NonIdrAccessUnit);

            Assert.True(writer.SequenceHeaderPrepended);
            Assert.True(writer.Statistics.HasParameterSets);
            Assert.Equal(2, writer.Statistics.AccessUnits);
        }

        Assert.Equal([.. header, .. bareIdr, .. H264Samples.NonIdrAccessUnit], File.ReadAllBytes(path));
    }

    [Fact]
    public void The_sequence_header_is_not_asked_for_when_the_stream_already_carries_one()
    {
        bool asked = false;
        using var writer = new ElementaryStreamWriter(NewPath())
        {
            SequenceHeaderProvider = () =>
            {
                asked = true;
                return H264Samples.Join(H264Samples.Sps, H264Samples.Pps);
            },
        };

        writer.WriteAccessUnit(H264Samples.IdrAccessUnit);

        Assert.False(asked);
        Assert.False(writer.SequenceHeaderPrepended);
    }

    [Fact]
    public void A_missing_sequence_header_is_reported_not_invented()
    {
        string path = NewPath();
        byte[] bareIdr = H264Samples.Join(H264Samples.IdrSlice);

        using (var writer = new ElementaryStreamWriter(path) { SequenceHeaderProvider = () => null })
        {
            writer.WriteAccessUnit(bareIdr);

            Assert.False(writer.SequenceHeaderPrepended);
            Assert.False(writer.Statistics.HasParameterSets);
            Assert.False(writer.Statistics.StartsWithDecodableKeyframe);
        }

        Assert.Equal(bareIdr, File.ReadAllBytes(path));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        string path = NewPath();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "keep me");

        Assert.Throws<IOException>(() => new ElementaryStreamWriter(path));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void A_run_that_wrote_nothing_leaves_no_empty_file_behind()
    {
        string path = NewPath();

        new ElementaryStreamWriter(path).Dispose();

        Assert.False(File.Exists(path));
    }

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.h264");
}
