using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Icons;

/// <summary>
/// Where an icon's pixels become something the compositor can draw: one device and one immediate
/// context, shared by every icon the canvas shows, handing out a small composition swap chain per
/// icon.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the route the in-box compositor accepts: it will not take a texture, shared or not,
/// while a swap chain presented once is exactly what its surface call wants. Two buffers are used
/// because a flip-model chain insists on at least two; the pixels are written into the first and
/// the chain presented before anyone sees it. What the chain holds is premultiplied BGRA, which is
/// the alpha form the compositor composites.
/// </para>
/// <para>
/// Belongs to the shell thread, like everything else that touches the compositor, and lives as long
/// as the canvas content: a remount reuses the device and makes fresh chains for its own icons. A
/// device that cannot be created is not fatal — the caller keeps drawing the item's glyph.
/// </para>
/// </remarks>
internal sealed class IconSurfaceDevice : IDisposable
{
    private const int CreateSwapChainForCompositionSlot = 24;
    private const int GetBufferSlot = 9;
    private const int UpdateSubresourceSlot = 48;
    private const int PresentSlot = 8;

    private readonly ILogger _logger;
    private readonly nint _device;
    private readonly nint _context;
    private readonly nint _factory;

    private bool _disposed;

    private IconSurfaceDevice(ILogger logger, nint device, nint context, nint factory)
    {
        _logger = logger;
        _device = device;
        _context = context;
        _factory = factory;
    }

    /// <summary>Makes the canvas its device, or returns null when this system cannot offer one.</summary>
    internal static IconSurfaceDevice? TryCreate(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var result = D3D11Interop.CreateDevice(
            nint.Zero,
            D3D11Interop.DriverTypeHardware,
            nint.Zero,
            D3D11Interop.CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11Interop.SdkVersion,
            out var device,
            out _,
            out var context);

        if (result < 0 || device == nint.Zero || context == nint.Zero)
        {
            Release(context);
            Release(device);
            logger.LogWarning("The icon surface device could not be created (0x{Result:X8}); items keep their glyphs", result);
            return null;
        }

        var factoryId = D3D11Interop.Factory2Id;
        result = D3D11Interop.CreateDXGIFactory2(0, in factoryId, out var factory);
        if (result < 0 || factory == nint.Zero)
        {
            Release(factory);
            Release(context);
            Release(device);
            logger.LogWarning("The icon surface factory could not be created (0x{Result:X8}); items keep their glyphs", result);
            return null;
        }

        return new IconSurfaceDevice(logger, device, context, factory);
    }

    /// <summary>
    /// A swap chain holding <paramref name="pixels"/> at the given size, already presented, or zero
    /// when it could not be made. The caller owns the chain and releases it when the icon leaves
    /// the screen.
    /// </summary>
    internal nint CreatePresentedSwapChain(uint width, uint height, byte[] pixels)
    {
        if (_disposed || width == 0 || height == 0 || pixels.Length < (long)width * height * 4)
        {
            return nint.Zero;
        }

        var description = new D3D11Interop.SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = D3D11Interop.FormatB8G8R8A8Unorm,
            Stereo = 0,
            SampleCount = 1,
            SampleQuality = 0,
            BufferUsage = D3D11Interop.UsageRenderTargetOutput,
            BufferCount = 2,
            Scaling = D3D11Interop.ScalingStretch,
            SwapEffect = D3D11Interop.SwapEffectFlipSequential,
            AlphaMode = D3D11Interop.AlphaModePremultiplied,
            Flags = 0,
        };

        var create = D3D11Interop.Method<D3D11Interop.CreateSwapChainForCompositionDelegate>(_factory, CreateSwapChainForCompositionSlot);
        var result = create(_factory, _device, ref description, nint.Zero, out var swapChain);
        if (result < 0 || swapChain == nint.Zero)
        {
            _logger.LogWarning("An icon swap chain of {Width}x{Height} could not be created (0x{Result:X8})", width, height, result);
            return nint.Zero;
        }

        if (!WritePixels(swapChain, width, height, pixels, out result))
        {
            Release(swapChain);
            return nint.Zero;
        }

        var present = D3D11Interop.Method<D3D11Interop.PresentDelegate>(swapChain, PresentSlot);
        result = present(swapChain, 1, 0);
        if (result < 0)
        {
            _logger.LogWarning("An icon swap chain could not present its pixels (0x{Result:X8})", result);
            Release(swapChain);
            return nint.Zero;
        }

        return swapChain;
    }

    /// <summary>Copies the pixels into the chain's first buffer; the one upload per icon.</summary>
    private bool WritePixels(nint swapChain, uint width, uint height, byte[] pixels, out int result)
    {
        var textureId = D3D11Interop.D3D11Texture2DId;
        var getBuffer = D3D11Interop.Method<D3D11Interop.GetBufferDelegate>(swapChain, GetBufferSlot);
        result = getBuffer(swapChain, 0, in textureId, out var texture);
        if (result < 0 || texture == nint.Zero)
        {
            _logger.LogWarning("An icon swap chain would not hand out its buffer (0x{Result:X8})", result);
            return false;
        }

        try
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var update = D3D11Interop.Method<D3D11Interop.UpdateSubresourceDelegate>(_context, UpdateSubresourceSlot);
                result = update(_context, texture, 0, nint.Zero, handle.AddrOfPinnedObject(), width * 4, width * height * 4);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            Release(texture);
        }

        if (result < 0)
        {
            _logger.LogWarning("An icon's pixels could not be uploaded (0x{Result:X8})", result);
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release(_factory);
        Release(_context);
        Release(_device);
    }

    private static void Release(nint pointer)
    {
        if (pointer != nint.Zero)
        {
            Marshal.Release(pointer);
        }
    }
}
