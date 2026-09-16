using Muralis.Core.Canvas;
using Muralis.Core.Models;

namespace Muralis.Core.Desktop;

/// <summary>
/// Finds a visible place for a newly added item: the free spot closest to the middle of the display
/// that no other free item has taken yet. Nothing is stored in pixels — the result is the anchor
/// offset the layout saves, so the choice survives a change of resolution or DPI.
/// </summary>
public static class DesktopItemPlacer
{
    /// <summary>The gap items are laid out with, in DIP; the seed row and the dock use the same idea.</summary>
    public const double SpacingDip = 130;

    /// <summary>
    /// The anchor offset for a new item, in DIP, measured from the display's centre (the anchor new
    /// items are given). Candidates are walked in rows outwards from the centre, so the first free
    /// spot is the one closest to where the user is looking. Items the dock shows are not in the way:
    /// they are not on this canvas at all.
    /// </summary>
    public static (double X, double Y) NextFreeSpot(
        IReadOnlyList<DesktopItem> items,
        IReadOnlyCollection<string>? dockedItemIds,
        double displayWidthDip,
        double displayHeightDip,
        double sizeDip)
    {
        ArgumentNullException.ThrowIfNull(items);

        var step = Math.Max(SpacingDip, sizeDip + 34);
        var centreX = displayWidthDip / 2.0;
        var centreY = displayHeightDip / 2.0;

        var taken = items
            .Where(item => item is not null && !(dockedItemIds?.Contains(item.Id) ?? false))
            .Select(item => CentreOf(item, displayWidthDip, displayHeightDip))
            .ToList();

        var columns = Math.Max(1, (int)((displayWidthDip - sizeDip) / step));
        var rows = Math.Max(1, (int)((displayHeightDip - sizeDip) / step));
        var halfColumns = columns / 2;
        var halfRows = rows / 2;

        var candidates = new List<(double X, double Y)>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                candidates.Add((
                    centreX + ((column - halfColumns) * step),
                    centreY + ((row - halfRows) * step)));
            }
        }

        foreach (var centre in candidates.OrderBy(spot => SquaredDistance(spot, centreX, centreY)))
        {
            if (!taken.Any(other => Math.Abs(other.X - centre.X) < step * 0.6 && Math.Abs(other.Y - centre.Y) < step * 0.6))
            {
                return (centre.X - centreX, centre.Y - centreY);
            }
        }

        // Every visible spot is taken: park the item in the middle and let the user drag it out.
        return (0, 0);
    }

    private static (double X, double Y) CentreOf(DesktopItem item, double displayWidthDip, double displayHeightDip)
    {
        var bounds = new PixelRect(0, 0, (int)Math.Round(displayWidthDip), (int)Math.Round(displayHeightDip));
        return CanvasAnchorMath.CenterOf(CanvasAnchorMath.PlaceItem(item, bounds, 1.0));
    }

    private static double SquaredDistance((double X, double Y) spot, double x, double y)
    {
        var dx = spot.X - x;
        var dy = spot.Y - y;
        return (dx * dx) + (dy * dy);
    }
}
