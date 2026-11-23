using ExtendSpace.Media.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace ExtendSpace.Media.Graphics;

/// <summary>
/// Converts captured BGRA textures to the NV12 textures an H.264 encoder takes, on the GPU, with the
/// Direct3D 11 video processor. Output is BT.709, limited range (16 to 235). The same pass scales
/// the picture when the encoded size differs from the source; where the picture does not fill the
/// encoded frame, the rest is black.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private const uint FormatSupportsInput = 0x1;  // D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT
    private const uint FormatSupportsOutput = 0x2; // D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT

    // D3D11_VIDEO_PROCESSOR_COLOR_SPACE bits: YCbCr_Matrix (bit 2) = 1 selects BT.709, and
    // Nominal_Range (bits 4-5) = 1 selects 16-235. All zero is full-range RGB.
    private const uint FullRangeRgb = 0;
    private const uint Bt709LimitedRange = (1u << 2) | (1u << 4);

    private readonly D3D11_VIDEO_PROCESSOR_STREAM[] _streams = new D3D11_VIDEO_PROCESSOR_STREAM[1];

    // Interface casts of the shared device and context; GraphicsDevice owns and releases those.
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;

    public unsafe Nv12Converter(GraphicsDevice graphics, OutputGeometry geometry, int frameRate)
    {
        try
        {
            _videoDevice = (ID3D11VideoDevice)graphics.Device;
            _videoContext = (ID3D11VideoContext)graphics.Context;
        }
        catch (InvalidCastException exception)
        {
            throw new InvalidOperationException(
                $"The graphics adapter '{graphics.AdapterName}' does not expose the Direct3D 11 video interfaces needed to convert BGRA to NV12.",
                exception);
        }

        try
        {
            var rate = new DXGI_RATIONAL { Numerator = (uint)frameRate, Denominator = 1 };
            var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
            {
                InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                InputFrameRate = rate,
                InputWidth = (uint)geometry.SourceWidth,
                InputHeight = (uint)geometry.SourceHeight,
                OutputFrameRate = rate,
                OutputWidth = (uint)geometry.OutputWidth,
                OutputHeight = (uint)geometry.OutputHeight,
                Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
            };

            _videoDevice.CreateVideoProcessorEnumerator(in content, out _enumerator);
            RequireFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, FormatSupportsInput, "BGRA input");
            RequireFormat(DXGI_FORMAT.DXGI_FORMAT_NV12, FormatSupportsOutput, "NV12 output");

            _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);

            var input = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { _bitfield = FullRangeRgb };
            var output = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { _bitfield = Bt709LimitedRange };
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, &input);
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, &output);
            _videoContext.VideoProcessorSetStreamFrameFormat(
                _processor,
                0,
                D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);

            // Driver "enhancements" (denoise, sharpening and the like) must not touch desktop pixels.
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);

            var source = new RECT { left = 0, top = 0, right = geometry.SourceWidth, bottom = geometry.SourceHeight };
            var picture = new RECT
            {
                left = geometry.PictureX,
                top = geometry.PictureY,
                right = geometry.PictureX + geometry.PictureWidth,
                bottom = geometry.PictureY + geometry.PictureHeight,
            };
            var frame = new RECT { left = 0, top = 0, right = geometry.OutputWidth, bottom = geometry.OutputHeight };
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, &source);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, &picture);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, &frame);

            // What is left of the frame around the picture, if anything, is opaque black, given in
            // the output's own terms: limited-range luma 16 with neutral chroma.
            var black = default(D3D11_VIDEO_COLOR);
            black.YCbCr.Y = 16f / 255;
            black.YCbCr.Cb = 128f / 255;
            black.YCbCr.Cr = 128f / 255;
            black.YCbCr.A = 1;
            _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, true, &black);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queues the conversion of <paramref name="source"/> into one slice of <paramref name="destination"/>.
    /// The GPU does the work asynchronously; this call returns once the command is submitted.
    /// </summary>
    public void Convert(ID3D11Texture2D source, ID3D11Texture2D destination, uint destinationSubresource)
    {
        ID3D11VideoProcessorInputView? inputView = null;
        ID3D11VideoProcessorOutputView? outputView = null;
        try
        {
            var inputDescription = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
            };
            _videoDevice.CreateVideoProcessorInputView(source, _enumerator, in inputDescription, out inputView);

            destination.GetDesc(out D3D11_TEXTURE2D_DESC destinationDescription);
            var outputDescription = default(D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC);
            if (destinationDescription.ArraySize > 1)
            {
                outputDescription.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
                outputDescription.Texture2DArray.FirstArraySlice = destinationSubresource;
                outputDescription.Texture2DArray.ArraySize = 1;
            }
            else
            {
                outputDescription.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
            }

            _videoDevice.CreateVideoProcessorOutputView(destination, _enumerator, in outputDescription, out outputView);

            _streams[0] = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true, pInputSurface = inputView };
            _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, _streams);
        }
        finally
        {
            _streams[0] = default;
            ComReference.Release(ref outputView);
            ComReference.Release(ref inputView);
        }
    }

    public void Dispose()
    {
        ComReference.Release(ref _processor);
        ComReference.Release(ref _enumerator);
    }

    private void RequireFormat(DXGI_FORMAT format, uint requiredSupport, string role)
    {
        _enumerator!.CheckVideoProcessorFormat(format, out uint support);
        if ((support & requiredSupport) == 0)
        {
            throw new InvalidOperationException(
                $"The Direct3D 11 video processor on this adapter does not support {role} ({format}).");
        }
    }
}
