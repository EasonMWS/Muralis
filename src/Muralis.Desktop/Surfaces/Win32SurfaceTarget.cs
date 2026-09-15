using Muralis.Core.Models;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// Desktop-internal target: what the shell hands content that renders natively and therefore needs
/// the window itself. The public <see cref="ISurfaceTarget"/> deliberately stays free of handles.
/// </summary>
internal sealed record Win32SurfaceTarget(
    nint WindowHandle,
    PixelRect PixelBounds,
    double ScaleFactor,
    SurfaceLayer Layer) : IWin32SurfaceTarget
{
    internal static Win32SurfaceTarget ForWindow(nint window, MonitorGeometry geometry, double scaleFactor) =>
        new(window, geometry.Bounds, scaleFactor, SurfaceLayer.WallpaperLayer);
}
