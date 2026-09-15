using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The minimum DXGI/D3D11 interop needed to give the media player a surface to draw into and to
/// show it in the host window. A blt-model (non flip) swap chain is used because flip model
/// swap chains cannot be attached to a child window.
/// </summary>
internal static class D3D11Interop
{
    internal const int DriverTypeHardware = 1;
    internal const uint CreateDeviceBgraSupport = 0x20;
    internal const uint FormatB8G8R8A8Unorm = 87;
    internal const uint SdkVersion = 7;
    internal const uint UsageRenderTargetOutput = 0x20;
    internal const uint SwapEffectDiscard = 0;

    internal static readonly Guid DxgiSurfaceId = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");

    /// <summary>
    /// <c>IDXGISwapChain1</c>. Its vtable starts with the <c>IDXGISwapChain</c> methods, so
    /// <see cref="Present"/> and <see cref="GetBuffer"/> sit at their documented slots. The plain
    /// <c>IDXGISwapChain</c> IID is not used because the value published in the old DXGI type
    /// library does not match what the runtime object answers to (verified by QueryInterface).
    /// </summary>
    [ComImport]
    [Guid("790A45F7-0D42-4876-983A-0A55CFE6F4AA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGISwapChainInterop
    {
        int SetPrivateData();

        int SetPrivateDataInterface();

        int GetPrivateData();

        int GetParent();

        int GetDevice();

        [PreserveSig]
        int Present(uint syncInterval, uint flags);

        void GetBuffer(uint buffer, ref Guid riid, out nint surface);
    }

    internal struct SwapChainDescription
    {
        public ModeDescription BufferDescription;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public nint OutputWindow;
        public int Windowed;
        public uint SwapEffect;
        public uint Flags;
    }

    internal struct ModeDescription
    {
        public uint Width;
        public uint Height;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDeviceAndSwapChain")]
    internal static extern int CreateDeviceAndSwapChain(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        ref SwapChainDescription swapChainDescription,
        out nint swapChain,
        out nint device,
        out nint featureLevel,
        out nint deviceContext);

    /// <summary>
    /// Wraps a raw <c>IDXGISurface</c> as the WinRT <c>IDirect3DSurface</c> the media player copies
    /// frames into. Exported flat from <c>d3d11.dll</c>; there is no managed projection for it.
    /// </summary>
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", ExactSpelling = true)]
    internal static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);
}
