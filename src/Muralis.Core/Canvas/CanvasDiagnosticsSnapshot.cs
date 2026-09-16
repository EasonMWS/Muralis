using Muralis.Core.Models;

namespace Muralis.Core.Canvas;

/// <summary>
/// What the desktop canvas looked like at one moment, for the development overlay. It is a value:
/// the canvas replaces it whenever anything changed, so a reader on another thread always sees a
/// complete, consistent picture without locking the canvas. <see cref="MonitorId"/> and
/// <see cref="SurfaceState"/> are filled in by the desktop layer, the only place that knows which
/// display the canvas sits on and what the host around it is doing.
/// </summary>
public sealed record CanvasDiagnosticsSnapshot(
    int MountCount,
    string LayoutPath,
    PixelRect BoundsPixels,
    double BoundsWidthDip,
    double BoundsHeightDip,
    double ScaleFactor,
    int Dpi,
    double PointerXDip,
    double PointerYDip,
    bool PointerInside,
    int ItemCount,
    string? HoveredItemId,
    double HoveredScale,
    string DockPhase,
    double DockScale,
    double UpdatesPerSecond,
    long Updates,
    string? MonitorId = null,
    string? SurfaceState = null);
