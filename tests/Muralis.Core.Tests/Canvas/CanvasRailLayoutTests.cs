using Muralis.Core.Canvas;
using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Canvas;

public sealed class CanvasRailLayoutTests
{
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);

    [Fact]
    public void LeftRail_SitsInsideTheMarginAndCentresVertically()
    {
        var dock = new CanvasDockOptions();

        var rail = CanvasRailLayout.RailRect(dock, Bounds, 1.0, itemCount: 4, expanded: true);

        var expectedLength = (4 * 56) + (3 * 16) + (2 * 10);
        Assert.Equal(10, rail.X);
        Assert.Equal(56 + 20, rail.Width);
        Assert.Equal(expectedLength, rail.Height);
        Assert.Equal((1080 - expectedLength) / 2, rail.Y);
    }

    [Fact]
    public void CollapsedRail_IsShiftedFullyPastTheEdge()
    {
        var dock = new CanvasDockOptions();

        var expanded = CanvasRailLayout.RailRect(dock, Bounds, 1.0, itemCount: 4, expanded: true);
        var collapsed = CanvasRailLayout.RailRect(dock, Bounds, 1.0, itemCount: 4, expanded: false);

        // Fully past the display's left edge: nothing of the rail is reachable or clickable.
        Assert.Equal(0, collapsed.X + collapsed.Width);
        Assert.True(expanded.X + expanded.Width > 0);
    }

    [Fact]
    public void RightEdgeRail_HugsTheOtherSide()
    {
        var dock = new CanvasDockOptions { Edge = CanvasDockEdge.Right };

        var rail = CanvasRailLayout.RailRect(dock, Bounds, 1.0, itemCount: 4, expanded: true);

        Assert.Equal(1920 - 10 - rail.Width, rail.X);
    }

    [Fact]
    public void SlotCenters_RunDownTheRailWithTheRestingSpacing()
    {
        var dock = new CanvasDockOptions();

        var centers = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);

        Assert.Equal(4, centers.Count);
        Assert.Equal(centers[0].Y + 72, centers[1].Y);
        Assert.Equal(centers[1].Y + 72, centers[2].Y);
        Assert.All(centers, center => Assert.Equal(10 + ((56 + 20) / 2.0), center.X));
    }

    [Fact]
    public void TooManyItems_StillLayOutInOrder()
    {
        var dock = new CanvasDockOptions();

        var centers = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 40);

        Assert.Equal(40, centers.Count);
        for (var i = 1; i < centers.Count; i++)
        {
            Assert.True(centers[i].Y > centers[i - 1].Y);
        }
    }

    [Fact]
    public void DisplacedCenters_AtRest_AreTheSlots()
    {
        var dock = new CanvasDockOptions();
        var slots = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);
        double[] scales = [1.0, 1.0, 1.0, 1.0];

        var centers = CanvasRailLayout.DisplacedCenters(dock, slots, scales, 1.0);

        Assert.Equal(slots, centers);
    }

    [Fact]
    public void DisplacedCenters_MagnifiedItem_KeepsItsNeighboursApart()
    {
        var dock = new CanvasDockOptions();
        var slots = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);
        double[] scales = [1.0, 1.5, 1.2, 1.0];

        var centers = CanvasRailLayout.DisplacedCenters(dock, slots, scales, 1.0);

        for (var i = 1; i < centers.Count; i++)
        {
            var gap = centers[i].Y - centers[i - 1].Y;
            var needed = ((dock.ItemSizeDip * scales[i]) + (dock.ItemSizeDip * scales[i - 1])) / 2;
            Assert.True(gap >= needed, $"items {i - 1} and {i} overlap: gap {gap} < {needed}");
        }
    }

    [Fact]
    public void DisplacedCenters_KeepTheRunCentredOnTheRestingRail()
    {
        var dock = new CanvasDockOptions();
        var slots = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);
        double[] scales = [1.0, 1.5, 1.2, 1.0];

        var centers = CanvasRailLayout.DisplacedCenters(dock, slots, scales, 1.0);

        var restingRunCenter = (slots[0].Y + slots[^1].Y) / 2;
        var grownRunCenter = (centers[0].Y + centers[^1].Y) / 2;
        Assert.Equal(restingRunCenter, grownRunCenter, precision: 6);
    }

    [Fact]
    public void DisplacedCenters_TheMagnifiedItemStaysNearestTheCursor()
    {
        // The item under the cursor grows; the run re-centres, so the grown item's centre is
        // pulled towards the middle of the rail - it must still be the item closest to the cursor.
        var dock = new CanvasDockOptions();
        var slots = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);
        double[] scales = [1.0, 1.5, 1.2, 1.0];
        var cursorY = slots[1].Y;

        var centers = CanvasRailLayout.DisplacedCenters(dock, slots, scales, 1.0);

        var closest = centers.OrderBy(center => Math.Abs(center.Y - cursorY)).First();
        Assert.Equal(centers[1], closest);
    }

    [Fact]
    public void HorizontalEdge_RunsAcrossInstead()
    {
        var dock = new CanvasDockOptions { Edge = CanvasDockEdge.Top };

        var rail = CanvasRailLayout.RailRect(dock, Bounds, 1.0, itemCount: 4, expanded: true);
        var centers = CanvasRailLayout.SlotCenters(dock, Bounds, 1.0, itemCount: 4);

        Assert.Equal(10, rail.Y);
        Assert.True(rail.Width > rail.Height);
        Assert.Equal(centers[0].X + 72, centers[1].X);
        Assert.All(centers, center => Assert.Equal(rail.Y + (rail.Height / 2.0), center.Y));
    }
}
