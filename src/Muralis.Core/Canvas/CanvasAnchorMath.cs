using Muralis.Core.Desktop;
using Muralis.Core.Models;

namespace Muralis.Core.Canvas;

/// <summary>
/// Turns anchor + DIP offsets into pixels. Positions are recomputed from the display bounds on
/// every layout pass; nothing pixel-shaped is ever persisted, which is what keeps a saved layout
/// sane across resolutions, DPI and display rearrangements.
/// </summary>
public static class CanvasAnchorMath
{
    /// <summary>The point of <paramref name="bounds"/> an anchor names.</summary>
    public static (double X, double Y) ResolvePoint(CanvasAnchor anchor, PixelRect bounds) => anchor switch
    {
        CanvasAnchor.TopLeft => (bounds.X, bounds.Y),
        CanvasAnchor.TopCenter => (bounds.X + bounds.Width / 2.0, bounds.Y),
        CanvasAnchor.TopRight => (bounds.X + bounds.Width, bounds.Y),
        CanvasAnchor.CenterLeft => (bounds.X, bounds.Y + bounds.Height / 2.0),
        CanvasAnchor.Center => (bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0),
        CanvasAnchor.CenterRight => (bounds.X + bounds.Width, bounds.Y + bounds.Height / 2.0),
        CanvasAnchor.BottomLeft => (bounds.X, bounds.Y + bounds.Height),
        CanvasAnchor.BottomCenter => (bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height),
        _ => (bounds.X + bounds.Width, bounds.Y + bounds.Height),
    };

    /// <summary>
    /// Which point of the item itself meets the anchor: (0, 0) is its top-left, (1, 1) its
    /// bottom-right. The offsets apply to that point, so e.g. a BottomRight item at (-40, -40)
    /// sits 40 DIP inside the bottom-right corner of the display.
    /// </summary>
    public static (double X, double Y) ResolveFactor(CanvasAnchor anchor) => anchor switch
    {
        CanvasAnchor.TopLeft => (0.0, 0.0),
        CanvasAnchor.TopCenter => (0.5, 0.0),
        CanvasAnchor.TopRight => (1.0, 0.0),
        CanvasAnchor.CenterLeft => (0.0, 0.5),
        CanvasAnchor.Center => (0.5, 0.5),
        CanvasAnchor.CenterRight => (1.0, 0.5),
        CanvasAnchor.BottomLeft => (0.0, 1.0),
        CanvasAnchor.BottomCenter => (0.5, 1.0),
        _ => (1.0, 1.0),
    };

    /// <summary>
    /// The pixel rectangle a free item occupies for the given display bounds and DPI scale,
    /// clamped so the whole item stays on the display.
    /// </summary>
    public static PixelRect PlaceItem(DesktopItem item, PixelRect bounds, double scaleFactor)
    {
        ArgumentNullException.ThrowIfNull(item);

        var (anchorX, anchorY) = ResolvePoint(item.Anchor, bounds);
        var (factorX, factorY) = ResolveFactor(item.Anchor);

        var size = Math.Max(1.0, item.SizeDip * scaleFactor);
        var x = anchorX + item.OffsetXDip * scaleFactor - size * factorX;
        var y = anchorY + item.OffsetYDip * scaleFactor - size * factorY;

        var sizePx = (int)Math.Round(size);
        return new PixelRect(
            (int)Math.Round(ClampInside(x, bounds.X, bounds.X + bounds.Width - size)),
            (int)Math.Round(ClampInside(y, bounds.Y, bounds.Y + bounds.Height - size)),
            sizePx,
            sizePx);
    }

    /// <summary>The centre of a placed item, in the same pixel space.</summary>
    public static (double X, double Y) CenterOf(PixelRect placed) =>
        (placed.X + placed.Width / 2.0, placed.Y + placed.Height / 2.0);

    /// <summary>
    /// The inverse of <see cref="PlaceItem"/>: the anchor offsets that put an item of
    /// <paramref name="sizeDip"/> at the given centre. Dragging uses it to turn the drop point
    /// back into anchor + DIP, so no pixel position is ever persisted.
    /// </summary>
    public static (double OffsetXDip, double OffsetYDip) OffsetForCenter(
        PixelRect bounds,
        double scaleFactor,
        CanvasAnchor anchor,
        double centerX,
        double centerY,
        double sizeDip)
    {
        var (anchorX, anchorY) = ResolvePoint(anchor, bounds);
        var (factorX, factorY) = ResolveFactor(anchor);
        var size = Math.Max(1.0, sizeDip) * scaleFactor;

        return (
            (centerX - anchorX - size * (0.5 - factorX)) / scaleFactor,
            (centerY - anchorY - size * (0.5 - factorY)) / scaleFactor);
    }

    private static double ClampInside(double value, double low, double high)
    {
        if (high < low)
        {
            return low;
        }

        return Math.Clamp(value, low, high);
    }
}
