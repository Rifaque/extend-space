using System.Runtime.InteropServices;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace ExtendSpace.Experiments.CaptureEncode.Encoding;

/// <summary>
/// A bounded pool of NV12 GPU textures wrapped as Media Foundation samples. An asynchronous encoder
/// keeps a sample until it has encoded it, so each frame needs its own surface; the surface returns
/// to the pool when the last reference to the sample is released.
/// </summary>
internal sealed class EncoderInputPool : IDisposable
{
    public const int InitialSurfaces = 4;
    public const int MaximumSurfaces = 16;

    private IMFVideoSampleAllocatorEx? _allocator;

    public unsafe EncoderInputPool(GraphicsDevice graphics, EncoderSettings settings)
    {
        IMFAttributes? attributes = null;
        IMFMediaType? type = null;
        try
        {
            PInvoke.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID, out void* allocator).ThrowOnFailure();
            try
            {
                _allocator = (IMFVideoSampleAllocatorEx)Marshal.GetObjectForIUnknown((nint)allocator);
            }
            finally
            {
                Marshal.Release((nint)allocator);
            }

            _allocator.SetDirectXManager(graphics.DeviceManager);

            PInvoke.MFCreateAttributes(out attributes, 3).ThrowOnFailure();

            // Render target: the video processor writes into the surface. Shader resource: the
            // encoder, or Media Foundation when it copies to system memory, reads from it.
            attributes.SetUINT32(
                PInvoke.MF_SA_D3D11_BINDFLAGS,
                (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE));
            attributes.SetUINT32(PInvoke.MF_SA_D3D11_USAGE, (uint)D3D11_USAGE.D3D11_USAGE_DEFAULT);
            attributes.SetUINT32(PInvoke.MF_SA_BUFFERS_PER_SAMPLE, 1);

            type = MediaTypes.CreateNv12Input(settings);
            _allocator.InitializeSampleAllocatorEx(InitialSurfaces, MaximumSurfaces, attributes, type);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            ComReference.Release(ref type);
            ComReference.Release(ref attributes);
        }
    }

    /// <summary>
    /// Takes a free surface. Returns false when the encoder still holds all of them. The caller
    /// releases both the sample and the texture.
    /// </summary>
    public unsafe bool TryAllocate(out IMFSample? sample, out ID3D11Texture2D? texture, out uint subresource)
    {
        sample = null;
        texture = null;
        subresource = 0;

        HRESULT result = _allocator!.AllocateSample(out IMFSample allocated);
        if (result == HRESULT.MF_E_SAMPLEALLOCATOR_EMPTY)
        {
            return false;
        }

        result.ThrowOnFailure();

        IMFMediaBuffer? buffer = null;
        try
        {
            allocated.GetBufferByIndex(0, out buffer);
            var dxgiBuffer = (IMFDXGIBuffer)buffer;

            dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID, out void* resource);
            try
            {
                texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)resource);
            }
            finally
            {
                Marshal.Release((nint)resource);
            }

            dxgiBuffer.GetSubresourceIndex(out subresource);

            // A surface buffer starts with a length of zero, which a transform reading it through
            // system memory would take to mean "empty".
            buffer.GetMaxLength(out uint length);
            buffer.SetCurrentLength(length);

            sample = allocated;
            return true;
        }
        catch
        {
            ComReference.Release(ref texture);
            ComReference.Release(allocated);
            throw;
        }
        finally
        {
            ComReference.Release(ref buffer);
        }
    }

    public void Dispose()
    {
        if (_allocator is not null)
        {
            _allocator.UninitializeSampleAllocator();
            ComReference.Release(ref _allocator);
        }
    }
}
