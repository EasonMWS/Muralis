using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;
using Windows.UI.Composition;

namespace Muralis.Desktop.Icons;

/// <summary>
/// One icon as the compositor draws it: the swap chain that carries its pixels and the brush the
/// canvas hangs on a visual. It owns both, and lets go of both together.
/// </summary>
internal sealed class IconSurface : IDisposable
{
    private readonly nint _swapChain;
    private bool _disposed;

    private IconSurface(nint swapChain, CompositionSurfaceBrush brush)
    {
        _swapChain = swapChain;
        Brush = brush;
    }

    internal CompositionSurfaceBrush Brush { get; }

    /// <summary>
    /// Turns a resolved icon into a surface, or returns null when the system will not have it — the
    /// item then keeps its glyph instead of the canvas losing the item.
    /// </summary>
    internal static IconSurface? TryCreate(Compositor compositor, IconSurfaceDevice device, IconBitmap bitmap, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(bitmap);

        var swapChain = device.CreatePresentedSwapChain((uint)bitmap.Width, (uint)bitmap.Height, bitmap.Pixels);
        if (swapChain == nint.Zero)
        {
            return null;
        }

        try
        {
            var surface = CompositionBootstrap.CreateSurfaceFromSwapChain(compositor, swapChain);
            return new IconSurface(swapChain, compositor.CreateSurfaceBrush(surface));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The icon of a desktop item could not be put on a surface");
            Marshal.Release(swapChain);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Brush.Dispose();
        Marshal.Release(_swapChain);
    }
}
