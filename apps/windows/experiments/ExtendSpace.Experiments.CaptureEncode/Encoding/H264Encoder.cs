using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using ExtendSpace.Experiments.CaptureEncode.Measurement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Experiments.CaptureEncode.Encoding;

/// <param name="data">One encoder output sample: Annex B bytes, valid only during the call.</param>
/// <param name="sampleTime">The timestamp of the input frame it came from, in 100 ns units.</param>
internal delegate void EncodedSampleHandler(ReadOnlySpan<byte> data, long sampleTime);

/// <summary>
/// One Media Foundation H.264 encoder transform (MFT), driven directly so that its output is the
/// raw elementary stream rather than a container. Handles both kinds of transform: asynchronous
/// (every hardware encoder), which asks for input and announces output through events, and
/// synchronous (Microsoft's software encoder), which is simply called.
///
/// Apart from the event pump, which only queues events, every member must be called from one thread.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    // MediaEventType values from mfobjects.h.
    private const uint EventError = 1;
    private const uint EventTransformNeedInput = 601;
    private const uint EventTransformHaveOutput = 602;
    private const uint EventTransformDrainComplete = 603;

    private const uint ProvidesSamples =
        (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
            | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES);

    private readonly ConcurrentQueue<(uint Type, HRESULT Status)> _events = new();
    private readonly Dictionary<long, long> _submitTimestamps = [];
    private readonly MFT_OUTPUT_DATA_BUFFER[] _output = new MFT_OUTPUT_DATA_BUFFER[1];
    private readonly List<string> _appliedSettings = [];
    private readonly EncoderSettings _settings;
    private readonly EncodedSampleHandler _onOutput;
    private readonly Action _wake;

    private IMFTransform? _transform;
    private Thread? _eventPump;
    private IMFSample? _callerSample;
    private IMFMediaBuffer? _callerBuffer;
    private uint _callerBufferSize;
    private int _inputRequests;

    private H264Encoder(EncoderSettings settings, EncodedSampleHandler onOutput, Action wake)
    {
        _settings = settings;
        _onOutput = onOutput;
        _wake = wake;
    }

    public EncoderEvidence Evidence { get; private set; } = null!;

    /// <summary>What each requested encoder property did, for the report.</summary>
    public IReadOnlyList<string> AppliedSettings => _appliedSettings;

    public bool IsAsync { get; private set; }

    /// <summary>Whether a frame may be submitted now. An asynchronous encoder has to ask first.</summary>
    public bool CanAcceptInput => !IsAsync || _inputRequests > 0;

    public bool DrainCompleted { get; private set; }

    /// <summary>Time from handing a frame to the encoder to receiving its encoded sample.</summary>
    public DurationSeries Turnaround { get; } = new();

    /// <summary>Output samples whose timestamp matched no submitted frame, so no turnaround could be measured.</summary>
    public long UnmatchedOutputs { get; private set; }

    /// <summary>Activates and configures the candidate. Throws <see cref="EncoderException"/> if it cannot be used.</summary>
    public static H264Encoder Open(
        EncoderCandidate candidate,
        GraphicsDevice graphics,
        EncoderSettings settings,
        EncodedSampleHandler onOutput,
        Action wake)
    {
        var encoder = new H264Encoder(settings, onOutput, wake);
        try
        {
            encoder.Initialize(candidate, graphics);
            return encoder;
        }
        catch (EncoderException)
        {
            encoder.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            // COM failures surface as several exception types (COMException, ArgumentException for
            // E_INVALIDARG, NotImplementedException for E_NOTIMPL, ...). Whatever it was, this
            // candidate is unusable and the caller moves on to the next one, keeping the reason.
            encoder.Dispose();
            throw new EncoderException(
                $"{exception.GetType().Name}: {exception.Message.Trim()} (0x{exception.HResult:X8})",
                exception);
        }
    }

    /// <summary>Gives the encoder one frame. Only valid while <see cref="CanAcceptInput"/> is true.</summary>
    public void Submit(IMFSample sample, long sampleTime)
    {
        sample.SetSampleTime(sampleTime);
        sample.SetSampleDuration(_settings.FrameDurationTicks);

        _submitTimestamps[sampleTime] = Stopwatch.GetTimestamp();
        HRESULT result = _transform!.ProcessInput(0, sample, 0);
        if (result.Failed)
        {
            _submitTimestamps.Remove(sampleTime);
            throw EncoderException.FromResult("IMFTransform::ProcessInput", result);
        }

        if (IsAsync)
        {
            _inputRequests--;
        }
        else
        {
            while (PullOutput())
            {
            }
        }
    }

    /// <summary>Handles everything the asynchronous encoder has announced since the last call.</summary>
    public void ProcessEvents()
    {
        while (_events.TryDequeue(out (uint Type, HRESULT Status) mediaEvent))
        {
            if (mediaEvent.Status.Failed)
            {
                throw EncoderException.FromResult($"The encoder reported a failed event (type {mediaEvent.Type}); it", mediaEvent.Status);
            }

            switch (mediaEvent.Type)
            {
                case EventTransformNeedInput:
                    _inputRequests++;
                    break;
                case EventTransformHaveOutput:
                    PullOutput();
                    break;
                case EventTransformDrainComplete:
                    DrainCompleted = true;
                    break;
                case EventError:
                    throw new EncoderException("The encoder raised MEError without a failure code.");
            }
        }
    }

    /// <summary>
    /// Asks the encoder to emit everything it still holds. A synchronous encoder has finished when
    /// this returns; an asynchronous one finishes when <see cref="DrainCompleted"/> becomes true
    /// after later calls to <see cref="ProcessEvents"/>.
    /// </summary>
    public void BeginDrain()
    {
        if (!IsAsync)
        {
            _transform!.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        }

        _transform!.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);

        if (!IsAsync)
        {
            while (PullOutput())
            {
            }

            DrainCompleted = true;
        }
    }

    /// <summary>
    /// The SPS and PPS as the encoder publishes them on its output type, or null if it publishes
    /// none. Needed only if the stream itself turns out not to start with them.
    /// </summary>
    public byte[]? GetSequenceHeader()
    {
        _transform!.GetOutputCurrentType(0, out IMFMediaType type);
        try
        {
            var attributes = (IMFAttributes)type;
            if (attributes.GetBlobSize(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, out uint size).Failed || size == 0)
            {
                return null;
            }

            byte[] header = new byte[size];
            attributes.GetBlob(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, header);
            return header;
        }
        finally
        {
            ComReference.Release(type);
        }
    }

    public void Dispose()
    {
        if (_transform is not null)
        {
            // Shutting the transform down is what makes the event pump's blocking GetEvent return.
            if (_transform is IMFShutdown shutdown)
            {
                shutdown.Shutdown();
            }

            _eventPump?.Join();
            _eventPump = null;

            _output[0] = default;
            ComReference.Release(ref _callerBuffer);
            ComReference.Release(ref _callerSample);
            Marshal.FinalReleaseComObject(_transform);
            _transform = null;
        }
    }

    private void Initialize(EncoderCandidate candidate, GraphicsDevice graphics)
    {
        candidate.Activate.ActivateObject(out IMFTransform transform);
        _transform = transform;

        IMFAttributes? attributes = null;
        bool acceptedD3DManager = false;
        bool isD3D11Aware = false;
        try
        {
            try
            {
                transform.GetAttributes(out attributes);
            }
            catch (NotImplementedException)
            {
                // A transform without an attribute store is by definition synchronous and not Direct3D-aware.
            }

            if (attributes is not null)
            {
                IsAsync = attributes.GetUINT32(PInvoke.MF_TRANSFORM_ASYNC, out uint isAsync).Succeeded && isAsync != 0;
                if (IsAsync)
                {
                    attributes.SetUINT32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                }

                isD3D11Aware = attributes.GetUINT32(PInvoke.MF_SA_D3D11_AWARE, out uint aware).Succeeded && aware != 0;
                if (isD3D11Aware)
                {
                    acceptedD3DManager = TrySetD3DManager(transform, graphics);
                }

                attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
            }
        }
        finally
        {
            ComReference.Release(ref attributes);
        }

        Evidence = new EncoderEvidence
        {
            Name = candidate.Name,
            Clsid = candidate.Clsid,
            EnumeratedAsHardware = candidate.EnumeratedAsHardware,
            HardwareUrl = candidate.HardwareUrl,
            HardwareVendorId = candidate.HardwareVendorId,
            IsAsync = IsAsync,
            IsD3D11Aware = isD3D11Aware,
            AcceptedD3DManager = acceptedD3DManager,
        };

        ApplyCodecSettings(transform);

        // The H.264 encoders require the output type first; the input type is validated against it.
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        try
        {
            outputType = MediaTypes.CreateH264Output(_settings);
            transform.SetOutputType(0, outputType, 0);
            inputType = MediaTypes.CreateNv12Input(_settings);
            transform.SetInputType(0, inputType, 0);
        }
        finally
        {
            ComReference.Release(ref inputType);
            ComReference.Release(ref outputType);
        }

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        if (IsAsync)
        {
            var generator = (IMFMediaEventGenerator)transform;
            _eventPump = new Thread(() => PumpEvents(generator))
            {
                IsBackground = true,
                Name = "H.264 encoder events",
            };
            _eventPump.Start();
        }
    }

    private static bool TrySetD3DManager(IMFTransform transform, GraphicsDevice graphics)
    {
        nint manager = Marshal.GetIUnknownForObject(graphics.DeviceManager);
        try
        {
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)manager);
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or NotImplementedException or NotSupportedException)
        {
            // Recorded as "did not accept the device manager"; the encoder can still be fed through
            // system memory, and the acceleration verdict then says its GPU use is unconfirmed.
            return false;
        }
        finally
        {
            Marshal.Release(manager);
        }
    }

    private void ApplyCodecSettings(IMFTransform transform)
    {
        if (transform is not ICodecAPI codec)
        {
            _appliedSettings.Add("The encoder does not implement ICodecAPI; rate control, GOP length, B-frames and low-latency mode were left at its defaults.");
            return;
        }

        Apply(codec, "CODECAPI_AVEncCommonRateControlMode", typeof(CODECAPI_AVEncCommonRateControlMode).GUID, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR, "CBR");
        Apply(codec, "CODECAPI_AVEncCommonMeanBitRate", typeof(CODECAPI_AVEncCommonMeanBitRate).GUID, (uint)_settings.BitrateBitsPerSecond, null);
        Apply(codec, "CODECAPI_AVLowLatencyMode", typeof(CODECAPI_AVLowLatencyMode).GUID, true, null);
        Apply(codec, "CODECAPI_AVEncMPVDefaultBPictureCount", typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID, 0u, null);
        Apply(codec, "CODECAPI_AVEncMPVGOPSize", typeof(CODECAPI_AVEncMPVGOPSize).GUID, (uint)_settings.KeyframeIntervalFrames, null);
    }

    private void Apply(ICodecAPI codec, string name, Guid property, object value, string? meaning)
    {
        string requested = meaning is null ? $"{value}" : $"{value} ({meaning})";

        if (codec.IsSupported(in property) != HRESULT.S_OK)
        {
            _appliedSettings.Add($"{name} = {requested}: not supported by this encoder, left at its default");
            return;
        }

        HRESULT set = codec.SetValue(in property, in value);
        if (set.Failed)
        {
            _appliedSettings.Add($"{name} = {requested}: rejected with 0x{set.Value:X8}, left at its default");
            return;
        }

        string readBack = codec.GetValue(in property, out object current).Succeeded
            ? $"reads back as {current}"
            : "could not be read back";
        _appliedSettings.Add($"{name} = {requested}: accepted, {readBack}");
    }

    /// <summary>Runs on its own thread: turns the transform's blocking event stream into a queue.</summary>
    private unsafe void PumpEvents(IMFMediaEventGenerator generator)
    {
        while (true)
        {
            HRESULT result = generator.GetEvent(MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS.MF_EVENT_FLAG_NONE, out IMFMediaEvent mediaEvent);
            if (result.Failed)
            {
                if (result != HRESULT.MF_E_SHUTDOWN)
                {
                    _events.Enqueue((EventError, result));
                    _wake();
                }

                return;
            }

            HRESULT status = default;
            mediaEvent.GetType(out uint type);
            mediaEvent.GetStatus(&status);
            ComReference.Release(mediaEvent);

            _events.Enqueue((type, status));
            _wake();
        }
    }

    /// <summary>Takes one output sample if the encoder has one. Returns false if it needs more input.</summary>
    private bool PullOutput()
    {
        IMFTransform transform = _transform!;
        transform.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO streamInfo);
        bool encoderAllocates = (streamInfo.dwFlags & ProvidesSamples) != 0;

        if (!encoderAllocates)
        {
            EnsureCallerSample(streamInfo.cbSize);
        }

        _output[0] = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = encoderAllocates ? null : _callerSample };
        HRESULT result = transform.ProcessOutput(0, _output, out _);
        IMFSample? produced = _output[0].pSample;
        object? attachedEvents = _output[0].pEvents;
        _output[0] = default;

        try
        {
            if (result == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                return false;
            }

            if (result == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                AcceptOutputTypeChange(transform);
                return true;
            }

            if (result.Failed)
            {
                throw EncoderException.FromResult("IMFTransform::ProcessOutput", result);
            }

            if (produced is null)
            {
                throw new EncoderException("IMFTransform::ProcessOutput succeeded but returned no sample.");
            }

            Deliver(produced, encoderAllocates);
            return true;
        }
        finally
        {
            ComReference.Release(attachedEvents);
            if (encoderAllocates && produced is not null)
            {
                // The encoder reuses its own samples; this one must go back to it now.
                Marshal.FinalReleaseComObject(produced);
            }
        }
    }

    private unsafe void Deliver(IMFSample sample, bool encoderAllocated)
    {
        long sampleTime = -1;
        try
        {
            sample.GetSampleTime(out sampleTime);
        }
        catch (COMException)
        {
            // No timestamp on the output: counted below as an output that could not be matched.
        }

        if (_submitTimestamps.Remove(sampleTime, out long submitted))
        {
            Turnaround.Add(Stopwatch.GetElapsedTime(submitted));
        }
        else
        {
            UnmatchedOutputs++;
        }

        IMFMediaBuffer buffer;
        if (encoderAllocated)
        {
            sample.ConvertToContiguousBuffer(out buffer);
        }
        else
        {
            buffer = _callerBuffer!;
        }

        try
        {
            buffer.Lock(out byte* data, out _, out uint length);
            try
            {
                _onOutput(new ReadOnlySpan<byte>(data, checked((int)length)), sampleTime);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            if (encoderAllocated)
            {
                ComReference.Release(buffer);
            }
            else
            {
                buffer.SetCurrentLength(0);
            }
        }
    }

    /// <summary>For an encoder that writes into a buffer the caller supplies: one buffer, reused.</summary>
    private void EnsureCallerSample(uint requiredSize)
    {
        // Some encoders report no size until they have seen input; an uncompressed frame is a safe bound.
        uint size = Math.Max(requiredSize, (uint)(_settings.Width * _settings.Height * 3 / 2));
        if (_callerSample is not null && _callerBufferSize >= size)
        {
            return;
        }

        ComReference.Release(ref _callerBuffer);
        ComReference.Release(ref _callerSample);

        PInvoke.MFCreateSample(out _callerSample).ThrowOnFailure();
        PInvoke.MFCreateMemoryBuffer(size, out _callerBuffer).ThrowOnFailure();
        _callerSample.AddBuffer(_callerBuffer);
        _callerBufferSize = size;
    }

    private static void AcceptOutputTypeChange(IMFTransform transform)
    {
        HRESULT result = transform.GetOutputAvailableType(0, 0, out IMFMediaType type);
        if (result.Failed)
        {
            throw EncoderException.FromResult("The encoder announced a new output format, but IMFTransform::GetOutputAvailableType", result);
        }

        try
        {
            transform.SetOutputType(0, type, 0);
        }
        finally
        {
            ComReference.Release(type);
        }
    }
}
