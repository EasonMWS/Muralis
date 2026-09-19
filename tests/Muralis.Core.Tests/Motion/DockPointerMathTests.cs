using Muralis.Core.Motion;
using Xunit;

namespace Muralis.Core.Tests.Motion;

/// <summary>
/// The pointer's side of the motion engine: where the cursor is, and whether it is on the dock at all.
/// </summary>
/// <remarks>
/// Both are pure arithmetic, which is the point: a pointer source that disagrees with the icon centres about
/// which coordinate space it is in, or a region that is the wrong size, is a bug that looks like "the wave is
/// offset" or "the dock will not wake up" and is otherwise very hard to see.
/// </remarks>
public sealed class DockPointerMathTests
{
    // The dock window as it actually measures on the reference display: 960 wide with its origin at x=800.
    private const int WindowOriginX = 800;
    private const int WindowOriginY = 1267;

    // The dock's own drawn bounds inside that window, from the UI Automation measurement in Stage B.
    private const double DockLeft = 205;
    private const double DockRight = 1055;
    private const double DockTop = 70;
    private const double DockBottom = 152;

    [Fact]
    public void TheCursorIsTranslatedIntoTheDocksOwnSpace()
    {
        // A cursor drawn over the dock's left edge is at the dock's left edge, whatever the window's origin is.
        var (x, y) = DockPointerMath.ToDockSpace(
            WindowOriginX + (int)DockLeft,
            WindowOriginY + (int)DockTop,
            WindowOriginX,
            WindowOriginY,
            1);

        Assert.Equal(DockLeft, x, 9);
        Assert.Equal(DockTop, y, 9);
    }

    [Fact]
    public void TheTranslationIsTheSameForEveryPointOnTheDock()
    {
        // The conversion is a translation and nothing else — no scale, no rounding that drifts along the run.
        for (var offset = 0; offset <= 850; offset += 17)
        {
            var (x, _) = DockPointerMath.ToDockSpace(WindowOriginX + offset, WindowOriginY, WindowOriginX, WindowOriginY, 1);
            Assert.Equal(offset, x, 9);
        }
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    public void PhysicalPixelsAreConvertedToDipsAtEverySupportedDisplayScale(double pixelsPerDip)
    {
        const double expectedX = 320;
        const double expectedY = 84;
        var screenX = WindowOriginX + (int)(expectedX * pixelsPerDip);
        var screenY = WindowOriginY + (int)(expectedY * pixelsPerDip);

        var (x, y) = DockPointerMath.ToDockSpace(
            screenX,
            screenY,
            WindowOriginX,
            WindowOriginY,
            pixelsPerDip);

        Assert.Equal(expectedX, x, 9);
        Assert.Equal(expectedY, y, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidScaleFallsBackToOne(double pixelsPerDip)
    {
        var (x, y) = DockPointerMath.ToDockSpace(812, 1291, 800, 1267, pixelsPerDip);

        Assert.Equal(12, x, 9);
        Assert.Equal(24, y, 9);
    }

    [Fact]
    public void APointerOnTheDockIsInsideTheRestingRegion()
    {
        var region = DockPointerMath.RestingRegion(DockLeft, DockTop, DockRight, DockBottom);

        Assert.True(DockPointerMath.Contains(region, 205, 70));
        Assert.True(DockPointerMath.Contains(region, 630, 110));
        Assert.True(DockPointerMath.Contains(region, 1055, 152));
    }

    [Fact]
    public void APointerAwayFromTheDockIsOutsideTheRestingRegion()
    {
        var region = DockPointerMath.RestingRegion(DockLeft, DockTop, DockRight, DockBottom);

        // Above the icons, which is empty space in the window and not part of the dock.
        Assert.False(DockPointerMath.Contains(region, 630, 10));

        // Beyond either end of the run.
        Assert.False(DockPointerMath.Contains(region, 100, 110));
        Assert.False(DockPointerMath.Contains(region, 1200, 110));

        // Off the screen the dock is on.
        Assert.False(DockPointerMath.Contains(region, 630, 900));
    }

    [Fact]
    public void TheMotionRegionIsWiderAndTallerThanTheRestingOne()
    {
        var reach = DockMotionEngine.ReserveFor(30, DockMotionProfile.Default.BaseIconSize, DockMotionProfile.Default.SpacingDip, DockMotionProfile.Default);
        var resting = DockPointerMath.RestingRegion(DockLeft, DockTop, DockRight, DockBottom);
        var motion = DockPointerMath.MotionRegion(DockLeft, DockTop, DockRight, DockBottom, reach, DockMotionProfile.Default.VerticalReserve);

        Assert.True(motion.Left < resting.Left, "the motion region must reach further to the left");
        Assert.True(motion.Right > resting.Right, "the motion region must reach further to the right");
        Assert.True(motion.Top < resting.Top, "the motion region must reach further up");

        // The bottom edge is the dock's own: the dock is anchored there and nothing grows below it.
        Assert.Equal(resting.Bottom, motion.Bottom, 9);

        // And the room really is the reserve, not a bit more or less.
        Assert.Equal(reach, resting.Left - motion.Left, 9);
        Assert.Equal(reach, motion.Right - resting.Right, 9);
        Assert.Equal(DockMotionProfile.Default.VerticalReserve, resting.Top - motion.Top, 9);
    }

    [Fact]
    public void ExpandedLeaveRegionIncludesOneSemanticExitMargin()
    {
        var profile = DockMotionProfile.Default;
        var region = DockPointerMath.ExpandedRegion(
            DockLeft,
            DockTop,
            DockRight,
            DockBottom,
            40,
            profile.VerticalReserve,
            profile.ExitMarginDip);

        Assert.Equal(DockLeft - 40 - profile.ExitMarginDip, region.Left, 9);
        Assert.Equal(DockRight + 40 + profile.ExitMarginDip, region.Right, 9);
        Assert.Equal(DockTop - profile.VerticalReserve - profile.ExitMarginDip, region.Top, 9);
        Assert.Equal(DockBottom + profile.ExitMarginDip, region.Bottom, 9);

        // Moving by a pixel across the visual edge remains expanded; leaving the margin does not.
        Assert.True(DockPointerMath.Contains(region, DockRight + 40 + 1, DockBottom));
        Assert.False(DockPointerMath.Contains(region, region.Right + 2, DockBottom));
    }

    [Fact]
    public void APointerChasingThePeakStaysInsideTheMotionRegion()
    {
        // This is what the larger region is for: the peak of the wave is drawn above the dock and, at the ends
        // of the run, outside it. A pointer following it must not fall out of the region and collapse the
        // window it is being drawn in.
        var profile = DockMotionProfile.Default;
        var reach = DockMotionEngine.ReserveFor(30, profile.BaseIconSize, profile.SpacingDip, profile);
        var motion = DockPointerMath.MotionRegion(DockLeft, DockTop, DockRight, DockBottom, reach, profile.VerticalReserve);

        // The top of the tallest icon the engine can draw, which is the icon box's bottom minus the peak.
        var peakTop = DockBottom - profile.PeakIconSize - profile.MaximumLift;
        Assert.True(DockPointerMath.Contains(motion, 630, peakTop), $"the peak at y={peakTop} must be inside the region");

        // And the outermost icon when the run is pushed as far as it goes.
        Assert.True(DockPointerMath.Contains(motion, DockLeft - reach, DockBottom - 10));
        Assert.True(DockPointerMath.Contains(motion, DockRight + reach, DockBottom - 10));
    }

    [Fact]
    public void ARegionWithNoExtentContainsNothing()
    {
        // What the region is before the dock has been measured. An unmeasured dock must not claim the pointer.
        var empty = DockPointerMath.RestingRegion(0, 0, 0, 0);

        Assert.True(empty.IsEmpty);
        Assert.False(DockPointerMath.Contains(empty, 0, 0));
        Assert.False(DockPointerMath.Contains(empty, 630, 110));
    }

    [Fact]
    public void TheBoundaryIsForgivingRatherThanFlickering()
    {
        // A pointer sitting on the edge must not flip between "on the dock" and "off it" as it moves by a
        // fraction of a pixel, and a cursor can be reported a pixel outside a window edge while drawn over it.
        var region = DockPointerMath.RestingRegion(DockLeft, DockTop, DockRight, DockBottom);

        Assert.True(DockPointerMath.Contains(region, DockLeft - DockPointerMath.RegionToleranceDip, DockTop));
        Assert.True(DockPointerMath.Contains(region, DockRight + DockPointerMath.RegionToleranceDip, DockBottom));

        // But only by that much: a pointer genuinely away is away.
        Assert.False(DockPointerMath.Contains(region, DockLeft - (DockPointerMath.RegionToleranceDip * 4), DockTop));
        Assert.False(DockPointerMath.Contains(region, DockRight + (DockPointerMath.RegionToleranceDip * 4), DockBottom));
    }

    [Fact]
    public void EveryPositionOnTheDockIsInsideTheMotionRegion()
    {
        // The motion region has to be a superset of the resting one, or a pointer could be on the dock and
        // outside the region that is supposed to keep it there.
        var profile = DockMotionProfile.Default;
        var reach = DockMotionEngine.ReserveFor(30, profile.BaseIconSize, profile.SpacingDip, profile);
        var motion = DockPointerMath.MotionRegion(DockLeft, DockTop, DockRight, DockBottom, reach, profile.VerticalReserve);

        for (var x = DockLeft; x <= DockRight; x += 5)
        {
            for (var y = DockTop; y <= DockBottom; y += 5)
            {
                Assert.True(
                    DockPointerMath.Contains(motion, x, y),
                    $"({x},{y}) is on the dock but outside the motion region");
            }
        }
    }

    [Fact]
    public void TheMathNeedsNoWindowToBeRight()
    {
        // The whole surface of this type is values in and a bool out: no Window, no pointer event, no dispatch.
        var methods = typeof(DockPointerMath)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => !method.IsSpecialName)
            .ToArray();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            foreach (var parameter in method.GetParameters())
            {
                var name = parameter.ParameterType.FullName ?? parameter.ParameterType.Name;
                Assert.DoesNotContain("Microsoft.UI", name, StringComparison.Ordinal);
                Assert.DoesNotContain("Windows.Foundation", name, StringComparison.Ordinal);
            }
        });
    }
}
