using System.Runtime.InteropServices;
using ExtendSpace.Experiments.CaptureEncode.Interop;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Media.MediaFoundation;
using WinRT;

namespace ExtendSpace.Experiments.CaptureEncode.Graphics;

/// <summary>
/// The one Direct3D 11 device shared by capture, colour conversion and (for a hardware encoder)
/// encoding, so that frames stay on the GPU between them.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IMFDXGIDeviceManager? _deviceManager;

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, D3D_FEATURE_LEVEL featureLevel)
    {
        _device = device;
        _context = context;
        FeatureLevel = featureLevel;

        // The encoder and the capture API call into this device from their own threads.
        ((ID3D11Multithread)context).SetMultithreadProtected(true);

        ((IDXGIDevice)device).GetAdapter(out IDXGIAdapter adapter);
        try
        {
            DXGI_ADAPTER_DESC description = adapter.GetDesc();
            AdapterName = description.Description.ToString();
            AdapterVendorId = description.VendorId;
            AdapterLuid = description.AdapterLuid;
        }
        finally
        {
            ComReference.Release(adapter);
        }
    }

    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(GraphicsDevice));

    public ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(GraphicsDevice));

    public D3D_FEATURE_LEVEL FeatureLevel { get; }

    public string AdapterName { get; }

    public uint AdapterVendorId { get; }

    public LUID AdapterLuid { get; }

    /// <summary>Creates a device on the default hardware adapter, with video support.</summary>
    public static GraphicsDevice Create()
    {
        ReadOnlySpan<D3D_FEATURE_LEVEL> levels =
        [
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
        ];

        HRESULT result = PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            levels,
            PInvoke.D3D11_SDK_VERSION,
            out ID3D11Device device,
            out D3D_FEATURE_LEVEL featureLevel,
            out ID3D11DeviceContext context);

        if (result.Failed)
        {
            throw new InvalidOperationException(
                $"No hardware Direct3D 11 device with video support could be created (D3D11CreateDevice returned 0x{result.Value:X8}).",
                Marshal.GetExceptionForHR(result.Value));
        }

        return new GraphicsDevice(device, context, featureLevel);
    }

    /// <summary>
    /// The Media Foundation device manager that lends this device to the encoder and to the encoder
    /// input surface allocator. Created on first use and owned by this object.
    /// </summary>
    public IMFDXGIDeviceManager DeviceManager
    {
        get
        {
            if (_deviceManager is null)
            {
                PInvoke.MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager).ThrowOnFailure();
                manager.ResetDevice(Device, resetToken);
                _deviceManager = manager;
            }

            return _deviceManager;
        }
    }

    /// <summary>Wraps the device for Windows.Graphics.Capture. The caller disposes the result.</summary>
    public IDirect3DDevice CreateWinRTDevice()
    {
        PInvoke.CreateDirect3D11DeviceFromDXGIDevice((IDXGIDevice)Device, out Windows.Win32.System.WinRT.IInspectable inspectable)
            .ThrowOnFailure();

        nint unknown = Marshal.GetIUnknownForObject(inspectable);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
            ComReference.Release(inspectable);
        }
    }

    public void Dispose()
    {
        ComReference.Release(ref _deviceManager);
        ComReference.Release(ref _context);
        ComReference.Release(ref _device);
    }
}
