using System.Diagnostics;

namespace ExtendSpace.Experiments.CaptureEncode.Bitstream;

/// <summary>
/// Writes encoder output to a raw H.264 elementary stream file: the Annex B bytes, one access unit
/// after another, with no container around them.
/// </summary>
internal sealed class ElementaryStreamWriter : IDisposable
{
    private readonly FileStream _file;

    /// <summary>Creates the file. Refuses to replace an existing one.</summary>
    public ElementaryStreamWriter(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
    }

    public string Path { get; }

    public H264StreamStatistics Statistics { get; } = new();

    /// <summary>
    /// Asked for the SPS and PPS if the first access unit turns out not to carry them, so that the
    /// file is decodable from its first byte.
    /// </summary>
    public Func<byte[]?>? SequenceHeaderProvider { get; set; }

    /// <summary>True if the SPS and PPS had to be written from the encoder's output type.</summary>
    public bool SequenceHeaderPrepended { get; private set; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> at the most recent write; zero before the first.</summary>
    public long LastWriteTimestamp { get; private set; }

    public void WriteAccessUnit(ReadOnlySpan<byte> data)
    {
        if (Statistics.AccessUnits == 0 && !CarriesParameterSets(data) && SequenceHeaderProvider?.Invoke() is { Length: > 0 } header)
        {
            _file.Write(header);
            Statistics.AddParameterSets(header);
            SequenceHeaderPrepended = true;
        }

        _file.Write(data);
        Statistics.AddAccessUnit(data);
        LastWriteTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Flushes and closes the file, and removes it if nothing was ever written to it.</summary>
    public void Dispose()
    {
        _file.Flush(flushToDisk: true);
        _file.Dispose();

        if (Statistics.TotalBytes == 0)
        {
            // Created by this object (CreateNew) and still empty: an empty .h264 would only mislead.
            File.Delete(Path);
        }
    }

    private static bool CarriesParameterSets(ReadOnlySpan<byte> data)
    {
        bool sps = false;
        bool pps = false;
        foreach (NalUnit unit in AnnexB.FindNalUnits(data))
        {
            int type = AnnexB.NalType(data[unit.Offset]);
            sps |= type == H264NalType.Sps;
            pps |= type == H264NalType.Pps;
        }

        return sps && pps;
    }
}
