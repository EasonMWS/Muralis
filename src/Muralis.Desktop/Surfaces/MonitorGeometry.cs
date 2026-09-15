using Muralis.Core.Models;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The pixel-space placement a surface occupies on its display, in virtual-desktop coordinates.
/// Work area excludes things like the taskbar; backdrops use the full bounds, interactive
/// content uses the work area.
/// </summary>
public readonly record struct MonitorGeometry(PixelRect Bounds, PixelRect WorkArea)
{
    public static MonitorGeometry From(MonitorRuntimeInfo runtime) => new(runtime.Bounds, runtime.WorkArea);
}
