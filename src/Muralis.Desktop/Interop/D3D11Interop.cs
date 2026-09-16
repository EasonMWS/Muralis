using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The minimum DXGI/D3D11 interop needed to give the media player a surface to draw into, to show
/// it in the host window, and to hand the canvas a picture the composition engine can draw. A
/// blt-model (non flip) swap chain is used for the player because flip model swap chains cannot be
/// attached to a child window; the canvas' icon surfaces go the other way — a composition swap
/// chain, which is the only kind the in-box compositor accepts (see
/// <see cref="CompositionBootstrap.CreateSurfaceFromSwapChain"/>).
/// </summary>
internal static class D3D11Interop
{
    internal const int DriverTypeHardware = 1;
    internal const uint CreateDeviceBgraSupport = 0x20;
    internal const uint FormatB8G8R8A8Unorm = 87;
    internal const uint SdkVersion = 7;
    internal const uint UsageRenderTargetOutput = 0x20;
    internal const uint SwapEffectDiscard = 0;
    internal const uint SwapEffectFlipSequential = 3;
    internal const uint ScalingStretch = 0;
    internal const uint AlphaModePremultiplied = 1;

    internal static readonly Guid DxgiSurfaceId = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
    internal static readonly Guid Factory2Id = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    internal static readonly Guid D3D11Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

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

    /// <summary>
    /// How a composition swap chain is described to the factory. Fixed rather than templated: the
    /// canvas wants a two-buffer premultiplied BGRA flip chain and nothing else.
    /// </summary>
    internal struct SwapChainDescription1
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public int Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public uint Scaling;
        public uint SwapEffect;
        public uint AlphaMode;
        public uint Flags;
    }

    /// <summary>
    /// One method out of an object's vtable. The calls the canvas needs are not all on the COM
    /// interfaces the framework can import, so the few that are missing go through their documented
    /// slot numbers; each delegate below names the method and the slot it sits at.
    /// </summary>
    internal static T Method<T>(nint instance, int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * nint.Size));
    }

    /// <summary><c>IDXGISwapChain::Present</c>: the swap chain's first method, after five inherited ones.</summary>
    internal delegate int PresentDelegate(nint swapChain, uint syncInterval, uint flags);

    /// <summary><c>IDXGISwapChain::GetBuffer</c>, the method right after Present.</summary>
    internal delegate int GetBufferDelegate(nint swapChain, uint buffer, in Guid riid, out nint surface);

    /// <summary>
    /// <c>IDXGIFactory2::CreateSwapChainForComposition</c> — the last method of the factory's most
    /// derived interface, so the highest slot. This is the only swap chain kind the in-box
    /// compositor turns into a surface.
    /// </summary>
    internal delegate int CreateSwapChainForCompositionDelegate(nint factory, nint device, ref SwapChainDescription1 description, nint restrictToOutput, out nint swapChain);

    /// <summary>
    /// <c>ID3D11DeviceContext::UpdateSubresource</c> — the context's 41st own method, after the
    /// three it inherits. Used once per icon to push its pixels into a swap chain buffer.
    /// </summary>
    internal delegate int UpdateSubresourceDelegate(nint context, nint resource, uint subresource, nint box, nint data, uint rowPitch, uint depthPitch);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    internal static extern int CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out nint featureLevel,
        out nint deviceContext);

    [DllImport("dxgi.dll")]
    internal static extern int CreateDXGIFactory2(uint flags, in Guid riid, out nint factory);
}
