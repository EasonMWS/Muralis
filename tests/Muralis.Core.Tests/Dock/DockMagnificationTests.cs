using Muralis.Core.Dock;
using Xunit;

namespace Muralis.Core.Tests.Dock;

/// <summary>
/// The magnifier. What matters here is that a magnified item never lands on its neighbour, that the
/// curve is continuous as the pointer travels, and that the run stays where the user left it instead
/// of creeping along the rail.
/// </summary>
public sealed class DockMagnificationTests
{
    private static DockOptions Dock() => new()
    {
        ItemSizeDip = 56,
        SpacingDip = 12,
        PaddingDip = 8,
        EdgeMarginDip = 10,
        MaxScale = 1.6,
        InfluenceRadiusDip = 130,
    };

    private static readonly double Centre = 540;

    private static DockMagnificationLayout Layout(double? pointer, int count = 6, DockOptions? dock = null)
    {
        var options = dock ?? Dock();
        var resting = DockGeometry.RestingCentres(options, Centre, count);
        return DockMagnification.Compute(pointer, resting, Centre, options);
    }

    [Fact]
    public void AnAbsentPointer_LeavesEveryItemItsNaturalSize()
    {
        var layout = Layout(null);

        Assert.All(layout.Items, item => Assert.Equal(1.0, item.Scale));
        Assert.Equal(DockGeometry.RestingRunDip(Dock(), 6), layout.RunLengthDip);
    }

    [Fact]
    public void AnAbsentPointer_LeavesEveryItemInItsRestingPlace()
    {
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 6);

        var layout = Layout(null);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(resting[i], layout.Items[i].CenterAlongDip, precision: 6);
        }
    }

    [Fact]
    public void TheItemUnderThePointer_IsTheLargest()
    {
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 6);

        var layout = Layout(resting[3]);

        Assert.Equal(dock.MaxScale, layout.Items[3].Scale, precision: 6);
        Assert.All(layout.Items, item => Assert.True(item.Scale <= dock.MaxScale + 1e-9));
        Assert.Equal(3, layout.Items.ToList().FindIndex(item => Math.Abs(item.Scale - dock.MaxScale) < 1e-9));
    }

    [Fact]
    public void TheScaleFallsOffContinuouslyWithDistance()
    {
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 6);

        var layout = Layout(resting[2]);

        Assert.True(layout.Items[2].Scale > layout.Items[1].Scale);
        Assert.True(layout.Items[1].Scale > layout.Items[0].Scale);
        Assert.True(layout.Items[3].Scale < layout.Items[2].Scale);

        // The first item is 136 DIP away, which is outside the 130 DIP radius.
        Assert.Equal(1.0, layout.Items[0].Scale, precision: 6);
    }

    [Fact]
    public void NoTwoItemsEverOverlap_HoweverLargeTheyGrow()
    {
        var dock = Dock();

        for (var step = 0; step <= 60; step++)
        {
            var pointer = Centre - 200 + (step * 6);
            var layout = Layout(pointer);

            for (var i = 1; i < layout.Items.Count; i++)
            {
                var left = layout.Items[i - 1];
                var right = layout.Items[i];
                var gap = right.CenterAlongDip - left.CenterAlongDip;
                var needed = ((dock.ItemSizeDip * left.Scale) + (dock.ItemSizeDip * right.Scale)) / 2;

                Assert.True(gap >= needed - 1e-9, $"at {pointer}: items {i - 1} and {i} overlap by {needed - gap:0.###}");
            }
        }
    }

    [Fact]
    public void TheRunStaysCentredOnTheRail_WhereverThePointerIs()
    {
        var dock = Dock();

        for (var step = 0; step <= 40; step++)
        {
            var layout = Layout(Centre - 160 + (step * 8));

            // The run as a whole is centred: as much of it before the rail's middle as after it.
            // A magnified item makes its own end of the run longer, which is why the middle of the
            // run is the middle of its two edges and not of its two centres.
            var first = layout.Items[0];
            var last = layout.Items[^1];
            var left = first.CenterAlongDip - (dock.ItemSizeDip * first.Scale / 2);
            var right = last.CenterAlongDip + (dock.ItemSizeDip * last.Scale / 2);

            Assert.Equal(Centre, (left + right) / 2, precision: 6);
        }
    }

    [Fact]
    public void ThePointer_OverAnItem_FindsThatItemLargestAndNearest()
    {
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 6);

        for (var index = 0; index < resting.Count; index++)
        {
            var layout = Layout(resting[index]);

            var nearest = layout.Items
                .Select((item, place) => (Index: place, Distance: Math.Abs(item.CenterAlongDip - resting[index])))
                .MinBy(pair => pair.Distance);
            var largest = layout.Items
                .Select((item, place) => (Index: place, item.Scale))
                .MaxBy(pair => pair.Scale);

            Assert.Equal(index, largest.Index);
            Assert.Equal(index, nearest.Index);
        }
    }

    [Fact]
    public void ThePointer_BetweenTwoItems_StillFindsOneOfThemNearest()
    {
        // The run re-centres as it grows, so a magnified item is pushed away from the pointer by
        // more than its neighbour is pulled towards it: right at the boundary between two items the
        // pointer can be over the gap between them. The two can only ever disagree by that boundary,
        // which is what keeps the dock honest under the pointer.
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 6);

        for (var step = 0; step <= 120; step++)
        {
            var pointer = resting[0] + (step * 3.0);
            var layout = Layout(pointer);

            var nearest = layout.Items
                .Select((item, place) => (Index: place, Distance: Math.Abs(item.CenterAlongDip - pointer)))
                .MinBy(pair => pair.Distance);
            var largest = layout.Items
                .Select((item, place) => (Index: place, item.Scale))
                .MaxBy(pair => pair.Scale);

            Assert.True(
                Math.Abs(largest.Index - nearest.Index) <= 1,
                $"at {pointer:0.##}: largest is {largest.Index}, nearest is {nearest.Index}");
        }
    }

    [Fact]
    public void AContinuousSweep_IsContinuous()
    {
        // No jump in any item's scale or place: a fast sweep must not leave the dock snapping.
        var previous = Layout(Centre - 200);

        for (var step = 1; step <= 200; step++)
        {
            var layout = Layout(Centre - 200 + (step * 2.0));

            for (var i = 0; i < layout.Items.Count; i++)
            {
                Assert.True(Math.Abs(layout.Items[i].Scale - previous.Items[i].Scale) < 0.25);
                Assert.True(Math.Abs(layout.Items[i].CenterAlongDip - previous.Items[i].CenterAlongDip) < 40);
            }

            previous = layout;
        }
    }

    [Fact]
    public void ItemsOutsideTheInfluenceRadius_AreNotTouchedAtAll()
    {
        var dock = Dock();
        var resting = DockGeometry.RestingCentres(dock, Centre, 8);

        // The pointer is on the first item: the far end of the dock is beyond the radius.
        var layout = Layout(resting[0]);

        Assert.Equal(1.0, layout.Items[^1].Scale);
    }

    [Fact]
    public void TheRunGrowsAndShrinks_ButOnlyByWhatIsMagnified()
    {
        var dock = Dock();

        var resting = Layout(null).RunLengthDip;
        var magnified = Layout(Centre).RunLengthDip;

        Assert.True(magnified > resting);
        Assert.True(magnified <= DockGeometry.MagnifiedRunDip(dock, 6) + 1e-9);
    }

    [Fact]
    public void AnEmptyDock_ComputesNothing()
    {
        var layout = Layout(Centre, count: 0);

        Assert.Empty(layout.Items);
        Assert.Equal(0, layout.RunLengthDip);
    }

    [Fact]
    public void ManyItems_StayInOrderAndApart()
    {
        var dock = Dock();

        for (var pointer = Centre - 700; pointer <= Centre + 700; pointer += 35)
        {
            var layout = Layout(pointer, count: 50);

            for (var i = 1; i < layout.Items.Count; i++)
            {
                Assert.True(layout.Items[i].CenterAlongDip > layout.Items[i - 1].CenterAlongDip);
            }
        }
    }

    [Fact]
    public void AGaussianDock_FallsOffMoreSharply()
    {
        var smooth = Dock();
        var gaussian = Dock();
        gaussian.Falloff = Muralis.Core.Canvas.ProximityFalloff.Gaussian;

        var resting = DockGeometry.RestingCentres(smooth, Centre, 6);
        var pointer = resting[3];
        var oneAway = Math.Abs(resting[3] - resting[2]);

        var smoothLayout = DockMagnification.Compute(pointer, resting, Centre, smooth);
        var gaussianLayout = DockMagnification.Compute(pointer, resting, Centre, gaussian);

        // Both put the pointer's own item at full size, and both have something to say about the
        // neighbour: the Gaussian says less.
        Assert.Equal(smooth.MaxScale, smoothLayout.Items[3].Scale, precision: 6);
        Assert.Equal(gaussian.MaxScale, gaussianLayout.Items[3].Scale, precision: 6);
        Assert.True(oneAway < smooth.InfluenceRadiusDip);
        Assert.True(gaussianLayout.Items[2].Scale < smoothLayout.Items[2].Scale);
    }
}
