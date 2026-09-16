using Muralis.Core.Models;

namespace Muralis.Core.Canvas;

/// <summary>
/// Geometry of the dock rail: where the rail sits on the display, where each item's slot centre
/// is, and how magnified items displace their neighbours. Pure math over the dock options, so the
/// renderer only animates what this computes.
/// </summary>
public static class CanvasRailLayout
{
    /// <summary>The rail's pixel rectangle for <paramref name="bounds"/>; when collapsed it is shifted off the edge.</summary>
    public static PixelRect RailRect(CanvasDockOptions dock, PixelRect bounds, double scaleFactor, int itemCount, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var item = (int)Math.Round(dock.ItemSizeDip * scaleFactor);
        var spacing = (int)Math.Round(dock.ItemSpacingDip * scaleFactor);
        var padding = (int)Math.Round(dock.PaddingDip * scaleFactor);
        var margin = (int)Math.Round(dock.EdgeMarginDip * scaleFactor);

        var length = itemCount <= 0
            ? (2 * padding) + item
            : (itemCount * item) + ((itemCount - 1) * spacing) + (2 * padding);
        var thickness = item + (2 * padding);

        var (x, y, width, height) = dock.Edge switch
        {
            CanvasDockEdge.Right => (bounds.X + bounds.Width - margin - thickness, bounds.Y + ((bounds.Height - length) / 2), thickness, length),
            CanvasDockEdge.Top => (bounds.X + ((bounds.Width - length) / 2), bounds.Y + margin, length, thickness),
            CanvasDockEdge.Bottom => (bounds.X + ((bounds.Width - length) / 2), bounds.Y + bounds.Height - margin - thickness, length, thickness),
            _ => (bounds.X + margin, bounds.Y + ((bounds.Height - length) / 2), thickness, length),
        };

        if (!expanded)
        {
            var hidden = thickness + margin;
            (x, y) = dock.Edge switch
            {
                CanvasDockEdge.Right => (x + hidden, y),
                CanvasDockEdge.Top => (x, y - hidden),
                CanvasDockEdge.Bottom => (x, y + hidden),
                _ => (x - hidden, y),
            };
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Resting centre of every rail slot, top to bottom (or left to right for horizontal edges),
    /// in virtual-desktop pixels.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> SlotCenters(CanvasDockOptions dock, PixelRect bounds, double scaleFactor, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(dock);

        var rail = RailRect(dock, bounds, scaleFactor, itemCount, expanded: true);
        var item = dock.ItemSizeDip * scaleFactor;
        var spacing = dock.ItemSpacingDip * scaleFactor;
        var padding = dock.PaddingDip * scaleFactor;
        var vertical = IsVertical(dock.Edge);

        var centers = new List<(double X, double Y)>(Math.Max(0, itemCount));
        var offset = padding + (item / 2.0);
        for (var i = 0; i < itemCount; i++)
        {
            centers.Add(vertical
                ? (rail.X + (rail.Width / 2.0), rail.Y + offset)
                : (rail.X + offset, rail.Y + (rail.Height / 2.0)));
            offset += item + spacing;
        }

        return centers;
    }

    /// <summary>
    /// Slot centres with magnified items packed apart: an item grows from its own centre and
    /// pushes everything after it by half its growth, and the whole run re-centres on the rail.
    /// Neighbours therefore keep at least their resting gap and never overlap.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> DisplacedCenters(
        CanvasDockOptions dock,
        IReadOnlyList<(double X, double Y)> slots,
        IReadOnlyList<double> scales,
        double scaleFactor)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(scales);

        var count = Math.Min(slots.Count, scales.Count);
        var item = dock.ItemSizeDip * scaleFactor;
        var vertical = IsVertical(dock.Edge);

        var growth = new double[count];
        var totalGrowth = 0.0;
        for (var i = 0; i < count; i++)
        {
            growth[i] = item * (Math.Max(1.0, scales[i]) - 1.0);
            totalGrowth += growth[i];
        }

        var centers = new List<(double X, double Y)>(count);
        var shift = 0.0;
        for (var i = 0; i < count; i++)
        {
            var displacement = shift + (growth[i] / 2.0) - (totalGrowth / 2.0);
            var slot = slots[i];
            centers.Add(vertical
                ? (slot.X, slot.Y + displacement)
                : (slot.X + displacement, slot.Y));
            shift += growth[i];
        }

        return centers;
    }

    /// <summary>True when the rail runs vertically (left and right edges).</summary>
    public static bool IsVertical(CanvasDockEdge edge) => edge is CanvasDockEdge.Left or CanvasDockEdge.Right;
}
