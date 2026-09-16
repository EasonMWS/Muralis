using Muralis.Core.Dock;
using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Dock;

/// <summary>
/// Where the rail and its items sit. One set of formulas serves all four edges, so the tests walk
/// every edge through the same questions, and the display's origin and scale are varied to prove
/// nothing assumes a monitor at (0, 0) or one DIP per pixel.
/// </summary>
public sealed class DockGeometryTests
{
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);

    /// <summary>A second, larger display at 150% that is not at the origin.</summary>
    private static readonly PixelRect SecondDisplay = new(1920, -240, 2560, 1440);

    private static DockOptions Dock(
        DockEdge edge = DockEdge.Left,
        double maxScale = 1.6,
        double peek = 4,
        double trigger = 4) => new()
        {
            Edge = edge,
            MaxScale = maxScale,
            PeekSizeDip = peek,
            TriggerThicknessDip = trigger,
            ItemSizeDip = 56,
            SpacingDip = 12,
            PaddingDip = 8,
            EdgeMarginDip = 10,
        };

    [Fact]
    public void TheRailSitsInsideTheMarginAndCentresOnTheEdge()
    {
        var dock = Dock();
        var run = DockGeometry.RestingRunDip(dock, 4);
        var rail = DockGeometry.RailRect(dock, Bounds, 1.0, DockGeometry.RailLengthDip(dock, run), reveal: 1);

        var expectedLength = (4 * 56) + (3 * 12) + (2 * 8);
        Assert.Equal(10, rail.X);
        Assert.Equal(56 + 16, rail.Width);
        Assert.Equal(expectedLength, rail.Height);
        Assert.Equal((1080 - expectedLength) / 2, rail.Y);
    }

    [Fact]
    public void EveryEdgeHugsItsOwnSideOfTheDisplay()
    {
        var dock = Dock();
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 4));
        var thickness = dock.RailThicknessDip;

        var left = DockGeometry.RailRect(Dock(DockEdge.Left), Bounds, 1.0, length, 1);
        var right = DockGeometry.RailRect(Dock(DockEdge.Right), Bounds, 1.0, length, 1);
        var top = DockGeometry.RailRect(Dock(DockEdge.Top), Bounds, 1.0, length, 1);
        var bottom = DockGeometry.RailRect(Dock(DockEdge.Bottom), Bounds, 1.0, length, 1);

        Assert.Equal(10, left.X);
        Assert.Equal(1920 - 10 - thickness, right.X);
        Assert.Equal(10, top.Y);
        Assert.Equal(1080 - 10 - thickness, bottom.Y);

        // The side edges run the long way, the others across it.
        Assert.Equal(length, left.Height);
        Assert.Equal(length, right.Height);
        Assert.Equal(length, top.Width);
        Assert.Equal(length, bottom.Width);
    }

    [Fact]
    public void EveryEdgeRunsItsItemsInTheSameOrder()
    {
        var dock = Dock();
        var centres = DockGeometry.RestingCentres(dock, 540, 4);

        Assert.Equal(4, centres.Count);
        Assert.Equal(centres[0] + 68, centres[1]);
        Assert.Equal(centres[1] + 68, centres[2]);
        Assert.Equal(centres[2] + 68, centres[3]);

        // Centred on the rail: as much before the first item as after the last.
        var run = DockGeometry.RestingRunDip(dock, 4);
        Assert.Equal(540 - (run / 2.0) + (dock.ItemSizeDip / 2.0), centres[0], precision: 6);
        Assert.Equal(540 + (run / 2.0) - (dock.ItemSizeDip / 2.0), centres[3], precision: 6);
    }

    [Fact]
    public void ARetractedRailLeavesOnlyItsPeekShowing()
    {
        var dock = Dock();
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 4));

        var shown = DockGeometry.RailRect(dock, Bounds, 1.0, length, reveal: 1);
        var hidden = DockGeometry.RailRect(dock, Bounds, 1.0, length, reveal: 0);

        Assert.Equal(4, hidden.X + hidden.Width);
        Assert.Equal(shown.Width, hidden.Width);
        Assert.True(shown.X + shown.Width > hidden.X + hidden.Width);
    }

    [Fact]
    public void ARetractedRailCanHideCompletely()
    {
        var dock = Dock(peek: 0);
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 4));

        var hidden = DockGeometry.RailRect(dock, Bounds, 1.0, length, reveal: 0);

        // Nothing of the rail is inside the display any more.
        Assert.Equal(0, hidden.X + hidden.Width);
    }

    [Fact]
    public void TheTriggerStripLiesAlongTheEdgesWholeLength()
    {
        var dock = Dock(trigger: 4);

        var left = DockGeometry.TriggerBand(dock, Bounds, 1.0);
        var bottom = DockGeometry.TriggerBand(Dock(DockEdge.Bottom, trigger: 4), Bounds, 1.0);

        Assert.Equal(new PixelRect(0, 0, 4, 1080), left);
        Assert.Equal(new PixelRect(0, 1080 - 4, 1920, 4), bottom);
    }

    [Fact]
    public void TheDockExtent_CoversEveryItemAtItsLargest()
    {
        var dock = Dock(maxScale: 2.0);
        var extent = DockGeometry.Extent(dock, Bounds, 1.0, itemCount: 4);

        var magnified = DockGeometry.MagnifiedRunDip(dock, 4) + (2 * dock.PaddingDip);

        Assert.Equal(magnified, extent.Height);
        Assert.True(extent.Width >= 56 * 2, "the magnified item is wider than the rail");
        Assert.True(extent.X <= 10, "the extent starts at the edge margin or deeper");
    }

    [Fact]
    public void AMonitorThatIsNotAtTheOriginAndNotAt100Percent_LandsWhereItReallyIs()
    {
        // 2560 x 1440 pixels at 150% is 1706.67 x 960 DIP, and its edge is not the primary one's.
        var dock = Dock();

        var centre = new DockFrame(dock.Edge, SecondDisplay, 1.5).AlongCentreDip;
        Assert.Equal(960 / 2.0, centre, precision: 6);

        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 4));
        var rail = DockGeometry.RailRect(dock, SecondDisplay, 1.5, length, reveal: 1);

        // The rail is 10 DIP in from this monitor's own left edge, in that monitor's pixels.
        Assert.Equal(1920 + 15, rail.X);
        Assert.Equal((int)Math.Round(dock.RailThicknessDip * 1.5), rail.Width);

        var band = DockGeometry.TriggerBand(dock, SecondDisplay, 1.5);
        Assert.Equal(1920, band.X);
        Assert.Equal((int)Math.Round(4 * 1.5), band.Width);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheDockExtent_CoversTheRailAndEveryItemAtEveryScale(double scale)
    {
        var dock = Dock();
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.MagnifiedRunDip(dock, 6));
        var shown = DockGeometry.RailRect(dock, SecondDisplay, scale, length, reveal: 1);
        var extent = DockGeometry.Extent(dock, SecondDisplay, scale, itemCount: 6);

        // Everything the dock draws -- the rail out or away, and an item at its largest -- is inside
        // the extent. It may reach past the edge the rail hides behind, which is harmless: a window
        // region is clipped to its own window.
        Assert.True(extent.X + extent.Width >= shown.X + shown.Width, "the rail is not covered across its thickness");
        Assert.True(extent.Y <= shown.Y, "the rail is not covered along its length");
        Assert.True(extent.Y + extent.Height >= shown.Y + shown.Height);
        Assert.True(extent.Width >= (int)Math.Round(dock.ItemSizeDip * dock.MaxScale * scale), "a magnified item does not fit");
        Assert.True(extent.X <= SecondDisplay.X + 10, "the extent is not anchored at the dock's own edge");
    }

    [Fact]
    public void TheTriggerStrip_IsReadFromWhicheverEdgeTheDockIsOn()
    {
        var dock = Dock(trigger: 6);

        foreach (var edge in DockEdgeInfo.All)
        {
            var forEdge = Dock(edge, trigger: 6);
            var frame = new DockFrame(edge, SecondDisplay, 1.0);
            var onTheEdge = frame.PointAt(0, 0);
            var sixDipIn = frame.PointAt(0, 6);

            var (_, edgeDepth) = DockGeometry.ToAlongAndDepth(frame, onTheEdge.X, onTheEdge.Y);
            var (_, innerDepth) = DockGeometry.ToAlongAndDepth(frame, sixDipIn.X, sixDipIn.Y);

            // A point taken from the frame's own edge has no depth; six DIP in from it does.
            Assert.Equal(0, edgeDepth, precision: 6);
            Assert.Equal(6, innerDepth, precision: 6);

            Assert.True(
                DockGeometry.InTriggerBand(forEdge, SecondDisplay, 1.0, onTheEdge.X, onTheEdge.Y),
                $"the strip is not reachable on {edge}");
            Assert.True(
                DockGeometry.InTriggerBand(forEdge, SecondDisplay, 1.0, sixDipIn.X, sixDipIn.Y),
                $"the strip is too thin on {edge}");

            var furtherIn = frame.PointAt(0, 30);
            Assert.False(
                DockGeometry.InTriggerBand(forEdge, SecondDisplay, 1.0, furtherIn.X, furtherIn.Y),
                $"the strip is wider than it says on {edge}");
        }
    }

    [Fact]
    public void RailContainment_FollowsTheDockOntoEveryEdge()
    {
        var dock = Dock();
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 2));
        var depth = DockGeometry.ItemCentreDepthDip(dock);

        foreach (var edge in DockEdgeInfo.All)
        {
            var forEdge = Dock(edge);
            var frame = new DockFrame(edge, Bounds, 1.0);
            var centre = frame.AlongCentreDip;
            var (insideX, insideY) = frame.PointAt(centre, depth);
            var (outsideX, outsideY) = frame.PointAt(centre, depth + dock.RailThicknessDip);

            Assert.True(
                DockGeometry.RailContains(forEdge, Bounds, 1.0, length, reveal: 1, insideX, insideY),
                $"the rail does not contain its own middle on {edge}");
            Assert.False(
                DockGeometry.RailContains(forEdge, Bounds, 1.0, length, reveal: 1, outsideX, outsideY),
                $"a point past the rail's thickness counts as inside on {edge}");
        }
    }

    [Fact]
    public void ARetractedRail_IsNotUnderThePointerAnyMore()
    {
        var dock = Dock(peek: 0);
        var length = DockGeometry.RailLengthDip(dock, DockGeometry.RestingRunDip(dock, 2));
        var depth = DockGeometry.ItemCentreDepthDip(dock);
        var frame = new DockFrame(dock.Edge, Bounds, 1.0);
        var (x, y) = frame.PointAt(frame.AlongCentreDip, depth);

        Assert.True(DockGeometry.RailContains(dock, Bounds, 1.0, length, reveal: 1, x, y));
        Assert.False(DockGeometry.RailContains(dock, Bounds, 1.0, length, reveal: 0, x, y));
    }

    [Fact]
    public void TheRailIsNeverThinnerThanOneItemRow()
    {
        var dock = Dock();

        // An empty dock still has a rail to drop something onto.
        Assert.Equal(dock.RailThicknessDip, DockGeometry.RailLengthDip(dock, 0));
    }

    [Fact]
    public void AnEmptyDock_HasNoItemsAndNoRun()
    {
        var dock = Dock();

        Assert.Empty(DockGeometry.RestingCentres(dock, 540, 0));
        Assert.Equal(0, DockGeometry.RestingRunDip(dock, 0));
        Assert.Equal(0, DockGeometry.MagnifiedRunDip(dock, 0));
    }
}
