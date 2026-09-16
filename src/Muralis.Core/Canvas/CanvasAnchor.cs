namespace Muralis.Core.Canvas;

/// <summary>
/// Which point of a display a canvas position is measured from. Layouts store an anchor plus DIP
/// offsets instead of pixels, so the same layout lands in a sensible place when the display
/// changes resolution, DPI or position.
/// </summary>
public enum CanvasAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    CenterLeft,
    Center,
    CenterRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}
