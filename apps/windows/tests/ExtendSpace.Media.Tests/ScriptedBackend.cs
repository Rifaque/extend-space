using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Media.Encoding;

namespace ExtendSpace.Media.Tests;

/// <summary>
/// Hand-built H.264 access units. The NAL payloads are placeholders: everything downstream of the
/// encoder looks only at start codes and NAL header bytes.
/// </summary>
internal static class H264Units
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    public static readonly byte[] Sps = [0x67, 100, 0x00, 40, 0xAC, 0x2B];
    public static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0x80];
    public static readonly byte[] IdrSlice = [0x65, 0x88, 0x84, 0x21, 0xA0];
    public static readonly byte[] NonIdrSlice = [0x41, 0x9A, 0x24, 0x6C, 0x41];

    /// <summary>A keyframe as hardware encoders emit it: parameter sets, then the IDR slice.</summary>
    public static byte[] Keyframe => Join(Sps, Pps, IdrSlice);

    public static byte[] Delta => Join(NonIdrSlice);

    public static bool IsKeyframe(ReadOnlySpan<byte> accessUnit)
    {
        foreach (NalUnit unit in AnnexB.FindNalUnits(accessUnit))
        {
            if (AnnexB.NalType(accessUnit[unit.Offset]) == H264NalType.IdrSlice)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Join(params byte[][] units) => [.. units.SelectMany(unit => StartCode.Concat(unit))];
}

/// <summary>The Stopwatch clock, moved by hand.</summary>
internal sealed class ManualTimestamp
{
    private long _now = Stopwatch.Frequency * 1000;

    public long Now => Interlocked.Read(ref _now);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _now, (long)(by.TotalSeconds * Stopwatch.Frequency));
}

/// <summary>
/// The backend seam, scripted. Every call is logged in order; every resource counts its own
/// disposal so that a test can require each to be released exactly once.
/// </summary>
internal sealed class ScriptedBackend(ManualTimestamp? clock = null) : IMediaBackend
{
    private readonly Lock _gate = new();
    private readonly List<string> _log = [];

    public ManualTimestamp Clock { get; } = clock ?? new ManualTimestamp();

    /// <summary>When set, opening a source waits for it: a slow device or capture item.</summary>
    public ManualResetEventSlim? AcquireGate { get; set; }

    /// <summary>Thrown by the next source opened, instead of opening it.</summary>
    public Exception? AcquireFailure { get; set; }

    public List<ScriptedSource> Sources { get; } = [];

    public ScriptedSource Source => Sources[^1];

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public void Record(string entry)
    {
        lock (_gate)
        {
            _log.Add(entry);
        }
    }

    public ICaptureSource OpenSource(VideoSourceRequest request, Action wake)
    {
        Record($"open source {request.MonitorIndex?.ToString() ?? "primary"}");
        AcquireGate?.Wait(TimeSpan.FromSeconds(30));
        if (AcquireFailure is { } failure)
        {
            throw failure;
        }

        var source = new ScriptedSource(this, wake);
        lock (_gate)
        {
            Sources.Add(source);
        }

        return source;
    }
}

internal sealed class ScriptedSource(ScriptedBackend backend, Action wake) : ICaptureSource
{
    private readonly Lock _gate = new();
    private ScriptedFrame? _pending;
    private int _nextFrame;
    private long _delivered;
    private long _replaced;

    public VideoSourceDescription Description { get; } = new(2560, 1600, 165);

    public IReadOnlyList<string> Details { get; } = ["scripted monitor"];

    public List<ScriptedEncoder> Encoders { get; } = [];

    public ScriptedEncoder Encoder => Encoders[^1];

    public List<ScriptedFrame> Frames { get; } = [];

    /// <summary>When set, opening an encoder waits for it.</summary>
    public ManualResetEventSlim? EncoderGate { get; set; }

    public Exception? EncoderFailure { get; set; }

    public string? Failure { get; set; }

    public bool Started { get; private set; }

    public int Stops { get; private set; }

    public int Disposals { get; private set; }

    public CaptureActivity Activity
    {
        get
        {
            lock (_gate)
            {
                return new CaptureActivity(_delivered, 0, _delivered, 0, _replaced, Frames.Count == 0 ? null : Frames[^1].Arrival);
            }
        }
    }

    public IFrameEncoder OpenEncoder(EncodingRequest request, EncodedOutputHandler onOutput, Action wake)
    {
        backend.Record($"open encoder {request.Width}x{request.Height}@{request.FrameRate} {request.BitrateBitsPerSecond}");
        EncoderGate?.Wait(TimeSpan.FromSeconds(30));
        if (EncoderFailure is { } failure)
        {
            throw failure;
        }

        var encoder = new ScriptedEncoder(backend, request, onOutput);
        Encoders.Add(encoder);
        return encoder;
    }

    public void Start()
    {
        backend.Record("capture start");
        Started = true;
    }

    public void Stop()
    {
        backend.Record("capture stop");
        Stops++;
        lock (_gate)
        {
            _pending?.Dispose();
            _pending = null;
        }
    }

    /// <summary>The screen changed: a new frame is handed over, replacing one not yet taken.</summary>
    public ScriptedFrame Arrive()
    {
        var frame = new ScriptedFrame(++_nextFrame, backend.Clock.Now);
        lock (_gate)
        {
            Frames.Add(frame);
            _delivered++;
            if (_pending is not null)
            {
                _replaced++;
                _pending.Dispose();
            }

            _pending = frame;
        }

        wake();
        return frame;
    }

    public bool TryTake([NotNullWhen(true)] out ICapturedFrame? frame)
    {
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
            return frame is not null;
        }
    }

    public void Dispose()
    {
        backend.Record("source dispose");
        Disposals++;
    }
}

internal sealed class ScriptedFrame(int id, long arrival) : ICapturedFrame
{
    public int Id { get; } = id;

    public long Arrival { get; } = arrival;

    public int Disposals { get; private set; }

    public void Dispose() => Disposals++;
}

/// <summary>
/// An encoder that encodes at once (synchronously) unless <see cref="HoldOutput"/> is set, in which
/// case output waits for the next <see cref="ProcessEvents"/>, as an asynchronous transform's does.
/// Its first picture, and any picture it was told to force, is an IDR picture unless
/// <see cref="IgnoreForce"/> is set.
/// </summary>
internal sealed class ScriptedEncoder(ScriptedBackend backend, EncodingRequest request, EncodedOutputHandler onOutput) : IFrameEncoder
{
    private readonly Queue<(byte[] Data, long SampleTime)> _held = [];
    private bool _forceNext;
    private bool _first = true;

    public EncodingRequest Request { get; } = request;

    public StreamSettings Settings { get; } = new(request.Width, request.Height, request.FrameRate, request.BitrateBitsPerSecond, "Scripted Encoder", true);

    public IReadOnlyList<string> Details { get; } = ["scripted encoder"];

    public bool CanAcceptInput { get; set; } = true;

    public bool HoldOutput { get; set; }

    public bool IgnoreForce { get; set; }

    /// <summary>Bytes added to every slice, for tests that need frames big enough to fill a socket.</summary>
    public int Padding { get; set; }

    /// <summary>When zero, every input surface is in use and a submit is refused.</summary>
    public int FreeSurfaces { get; set; } = int.MaxValue;

    public Exception? SubmitFailure { get; set; }

    public Exception? EventFailure { get; set; }

    /// <summary>What a keyframe is, in place of the placeholder; for tests that need a real SPS.</summary>
    public byte[]? KeyframeOutput { get; set; }

    /// <summary>When set, every output is checked before it is handed on, as the real encoder adapter does (D57).</summary>
    public EncodedOutputCheck? OutputCheck { get; set; }

    public string? ProducedStream => OutputCheck?.First?.ToString();

    public List<(int Frame, long SampleTime, bool Forced)> Submitted { get; } = [];

    public int Forces { get; private set; }

    public int Disposals { get; private set; }

    public void ProcessEvents()
    {
        if (EventFailure is { } failure)
        {
            throw failure;
        }

        while (_held.TryDequeue(out (byte[] Data, long SampleTime) output))
        {
            OutputCheck?.Verify(output.Data);
            onOutput(output.Data, output.SampleTime);
        }
    }

    public bool ForceKeyframe()
    {
        Forces++;
        _forceNext = true;
        return true;
    }

    public bool Submit(ICapturedFrame frame, long sampleTime)
    {
        if (SubmitFailure is { } failure)
        {
            throw failure;
        }

        if (FreeSurfaces == 0)
        {
            return false;
        }

        var scripted = (ScriptedFrame)frame;
        Assert.Equal(0, scripted.Disposals);
        Assert.True(Submitted.Count == 0 || sampleTime > Submitted[^1].SampleTime, "sample times must only go forward");
        bool keyframe = _first || (_forceNext && !IgnoreForce);
        Submitted.Add((scripted.Id, sampleTime, _forceNext));
        backend.Record($"encode frame {scripted.Id}{(keyframe ? " as keyframe" : string.Empty)}");
        _first = false;
        _forceNext = false;

        byte[] output = keyframe ? KeyframeOutput ?? H264Units.Keyframe : H264Units.Delta;
        if (Padding > 0)
        {
            output = [.. output, .. Enumerable.Repeat((byte)0xAA, Padding)];
        }

        if (HoldOutput)
        {
            _held.Enqueue((output, sampleTime));
        }
        else
        {
            OutputCheck?.Verify(output);
            onOutput(output, sampleTime);
        }

        return true;
    }

    public void Dispose()
    {
        backend.Record("encoder dispose");
        Disposals++;
    }
}

/// <summary>Collects what the pipeline hands to its sink.</summary>
internal sealed class RecordingSink
{
    private readonly Lock _gate = new();
    private readonly List<(bool Keyframe, long CaptureMicros, int Length)> _units = [];

    public EncodedVideoSink Sink => (unit, captureMicros) =>
    {
        lock (_gate)
        {
            _units.Add((H264Units.IsKeyframe(unit), captureMicros, unit.Length));
        }
    };

    public IReadOnlyList<(bool Keyframe, long CaptureMicros, int Length)> Units
    {
        get
        {
            lock (_gate)
            {
                return [.. _units];
            }
        }
    }

    public int Keyframes => Units.Count(unit => unit.Keyframe);
}
