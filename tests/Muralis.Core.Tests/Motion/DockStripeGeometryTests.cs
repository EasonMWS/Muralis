using Muralis.Core.Motion;
using Xunit;

namespace Muralis.Core.Tests.Motion;

/// <summary>
/// The Dock's product geometry: content-sized, bottom-centred, and reserving exactly the room the
/// magnification is drawn into.
/// </summary>
/// <remarks>
/// These are the rules a running dock cannot be trusted to state about itself. Every one of them is
/// checked here as arithmetic, so a change to the profile, the icon box or the plate is caught before it
/// reaches a window. The last one re-derives the whole chain the renderer uses, which is what makes
/// "the peak is never clipped" a measurement rather than an intention.
/// </remarks>
public sealed class DockStripeGeometryTests
{
    private static readonly DockWorkArea FullHd = new(0, 0, 1920, 1080);

    /// <summary>A 4K work area, which is where a wide dock has room to be wide.</summary>
    private static readonly DockWorkArea UltraHd = new(0, 0, 3840, 2120);

    public static TheoryData<int> AppCounts => new() { 0, 1, 3, 6, 10, 12 };

    /// <summary>The width the run of icons occupies for a given number of pins, in DIP.</summary>
    private static double ContentWidth(int count) =>
        count <= 0
            ? 0
            : (count * DockStripeGeometry.IconHostDip) + ((count - 1) * DockStripeGeometry.IconSpacingDip);

    [Fact]
    public void TheStripeIsTallEnoughForTheIconBoxAndTheRoomAPeakGrowsInto()
    {
        var expected = DockStripeGeometry.VerticalReserveDip
            + DockStripeGeometry.IconHostDip
            + DockStripeGeometry.PlatePaddingY;

        Assert.Equal(expected, DockStripeGeometry.StripeHeightDip, 6);

        // The reserve is the growth of the square the engine scales, not of the artwork inside it: the
        // artwork is smaller, and sizing the stripe from it would clip the peak by the difference.
        var artworkReserve = DockMotionProfile.Default.VerticalReserve;
        Assert.True(
            DockStripeGeometry.VerticalReserveDip > artworkReserve,
            $"the reserve must follow the {DockStripeGeometry.IconHostDip} DIP icon box, but {DockStripeGeometry.VerticalReserveDip} is no more than the artwork's {artworkReserve}");
    }

    [Fact]
    public void TheRestingWidthFollowsTheAppsOnTheDock()
    {
        var narrow = DockStripeGeometry.Resting(ContentWidth(3), UltraHd, 1);
        var middle = DockStripeGeometry.Resting(ContentWidth(6), UltraHd, 1);
        var wide = DockStripeGeometry.Resting(ContentWidth(10), UltraHd, 1);

        Assert.True(narrow.Width < middle.Width, "six apps must make a wider dock than three");
        Assert.True(middle.Width < wide.Width, "ten apps must make a wider dock than six");

        // Content-sized means just that: three apps may not reserve a strip sized for twenty. The plate is
        // the run plus its own padding, so the bound is tight on purpose.
        foreach (var (count, stripe) in new[] { (3, narrow), (6, middle), (10, wide) })
        {
            var plate = ContentWidth(count) + (2 * DockStripeGeometry.PlatePaddingX);
            Assert.InRange(stripe.Width, plate, plate + 1);
        }
    }

    [Fact]
    public void TheDockStaysCentredAndTheIconsDoNotMoveWhenTheWindowGrows()
    {
        const int count = 6;
        var content = ContentWidth(count);
        var resting = DockStripeGeometry.Resting(content, UltraHd, 1);
        var expanded = DockStripeGeometry.Expanded(content, count, UltraHd, 1);

        // Centred in the work area, in both sizes.
        Assert.Equal(UltraHd.X + ((UltraHd.Width - resting.Width) / 2), resting.X);
        Assert.Equal(UltraHd.X + ((UltraHd.Width - expanded.Width) / 2), expanded.X);

        // The stripe is inside the expanded window, centred, so the icons' own screen position is the same
        // in both. This is the whole reason the reserve is taken from both sides at once. The comparison
        // allows a pixel: the window and the plate are whole physical pixels, so an odd width centres
        // within one of the ideal position and the user cannot see it.
        var restingRunLeft = resting.X + DockStripeGeometry.PlatePaddingX;
        var expandedRunLeft = expanded.X
            + ((expanded.Width - resting.Width) / 2)
            + DockStripeGeometry.PlatePaddingX;
        Assert.InRange(expandedRunLeft, restingRunLeft - 1, restingRunLeft + 1);

        // The stripe must also actually fit inside the expanded window, or the plate would be clipped by
        // the window that was grown for it.
        Assert.True(expanded.Width >= resting.Width, "the expanded window must hold the resting plate");
        Assert.True(
            expanded.X <= expandedRunLeft - DockStripeGeometry.PlatePaddingX + 1,
            "the plate must not start left of the window it is drawn in");
        Assert.True(
            expandedRunLeft + ContentWidth(count) + DockStripeGeometry.PlatePaddingX <= expanded.Right + 1,
            "the plate must not run past the window it is drawn in");

        // And the height does not change, so the baseline cannot move either.
        Assert.Equal(resting.Height, expanded.Height);
        Assert.Equal(resting.Y, expanded.Y);
    }

    [Fact]
    public void TheDockSitsLowOnTheScreen()
    {
        var stripe = DockStripeGeometry.Resting(ContentWidth(6), UltraHd, 1);

        // The icons' feet are PlatePaddingY plus BottomGapDip above the bottom of the work area. The dock
        // is meant to hug the bottom edge, so this is a small number and it is asserted rather than assumed.
        Assert.Equal(
            UltraHd.Height - (DockStripeGeometry.BottomGapDip + DockStripeGeometry.PlatePaddingY),
            stripe.Bottom);

        Assert.True(
            DockStripeGeometry.BottomGapDip + DockStripeGeometry.PlatePaddingY <= 20,
            "the dock must hug the bottom of the screen rather than float above it");
    }

    [Fact]
    public void NothingPinnedCollapsesTheStripeToTheProductFloor()
    {
        var empty = DockStripeGeometry.Resting(0, UltraHd, 1);

        // An empty dock is not a panel: there is no plate wider than one icon, and the host hides the
        // window entirely at this width. What matters for the arithmetic is that it does not scale with a
        // dock the user does not have.
        Assert.Equal((int)DockStripeGeometry.MinimumPlateWidthDip, empty.Width);
        Assert.True(empty.Width < DockStripeGeometry.Resting(ContentWidth(1), UltraHd, 1).Width + 1);
    }

    [Fact]
    public void TheDockNeverBecomesWiderThanTheWorkAreaItSitsOn()
    {
        var narrow = new DockWorkArea(0, 0, 700, 600);
        var stripe = DockStripeGeometry.Expanded(ContentWidth(12), 12, narrow, 1);

        Assert.True(stripe.Width <= narrow.Width, "the dock must not be wider than the screen it is on");
        Assert.True(stripe.X >= narrow.X, "the dock must not start off the left of the screen");
        Assert.True(stripe.X + stripe.Width <= narrow.Width, "the dock must not run off the right of the screen");
    }

    [Fact]
    public void TheSizeIsInPhysicalPixelsAtTheDisplaysScale()
    {
        var at100 = DockStripeGeometry.Resting(ContentWidth(6), UltraHd, 1);
        var at150 = DockStripeGeometry.Resting(ContentWidth(6), UltraHd, 1.5);

        Assert.True(at150.Width > at100.Width);
        Assert.True(at150.Height > at100.Height);
        Assert.Equal(Math.Ceiling(DockStripeGeometry.StripeHeightDip * 1.5), at150.Height);

        // An unusable scale is not a reason to size the dock to nothing.
        Assert.Equal(at100, DockStripeGeometry.Resting(ContentWidth(6), UltraHd, double.NaN));
        Assert.Equal(at100, DockStripeGeometry.Resting(ContentWidth(6), UltraHd, 0));
    }

    /// <summary>
    /// The acceptance rule the reserve exists for, re-derived from the renderer's own chain: at the worst
    /// pointer position, at full scale and full lift, the magnified icon is inside the surface it is drawn
    /// on. Nothing here reuses a number the geometry produced -the icon's box is rebuilt from the profile
    /// and the engine's output, which is what makes this a check rather than a restatement.
    /// </summary>
    [Theory]
    [MemberData(nameof(AppCounts))]
    public void ThePeakOfTheWaveFitsInsideTheSurfaceAtEveryAppCount(int count)
    {
        if (count == 0)
        {
            return;
        }

        var profile = DockMotionProfile.Default;
        var stripe = DockStripeGeometry.Expanded(ContentWidth(count), count, UltraHd, 1);

        // The plate is centred in the expanded window, and the icons sit on the bottom of the plate.
        var plateLeft = stripe.X + ((stripe.Width - (ContentWidth(count) + (2 * DockStripeGeometry.PlatePaddingX))) / 2);
        var runLeft = plateLeft + DockStripeGeometry.PlatePaddingX;
        var boxBottom = stripe.Bottom - DockStripeGeometry.PlatePaddingY;

        var pitch = DockStripeGeometry.IconHostDip + DockStripeGeometry.IconSpacingDip;
        var centres = new double[count];
        var widths = new double[count];
        for (var i = 0; i < count; i++)
        {
            centres[i] = (i * pitch) + (DockStripeGeometry.IconHostDip / 2);
            widths[i] = DockStripeGeometry.IconHostDip;
        }

        var layout = new DockMotionLayout(centres, widths);
        var engine = new DockMotionEngine(count);

        // Every position the pointer can take: on an icon, and halfway between two of them -the case where
        // both sides of the wave take their full share at once.
        for (var i = 0; i < count; i++)
        {
            foreach (var pointer in new[] { centres[i], (centres[i] + centres[Math.Min(i + 1, count - 1)]) / 2 })
            {
                engine.Apply(layout, pointer, profile);

                for (var index = 0; index < count; index++)
                {
                    var sample = engine.Samples[index];
                    var scale = sample.Scale;
                    var centre = runLeft + centres[index] + sample.TranslateX;
                    var half = (DockStripeGeometry.IconHostDip * scale) / 2;

                    Assert.True(
                        centre - half >= stripe.X - 0.5,
                        $"{count} apps, pointer at {pointer}: icon {index} is drawn at {centre - half}, left of the surface at {stripe.X}");

                    Assert.True(
                        centre + half <= stripe.Right + 0.5,
                        $"{count} apps, pointer at {pointer}: icon {index} is drawn at {centre + half}, right of the surface at {stripe.Right}");

                    // The engine scales the icon box about its own bottom-centre, so the top edge rises by
                    // the growth and then by the lift. Its feet do not move.
                    var top = boxBottom - (DockStripeGeometry.IconHostDip * scale) - sample.Lift;
                    Assert.True(
                        top >= stripe.Y - 0.5,
                        $"{count} apps, pointer at {pointer}: icon {index} is drawn at {top}, above the surface at {stripe.Y}");
                }
            }
        }
    }

    [Fact]
    public void AStripeIsAlwaysARealRectangle()
    {
        foreach (var area in new[] { FullHd, UltraHd, new DockWorkArea(0, 0, 320, 200) })
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                foreach (var count in new[] { 0, 1, 12 })
                {
                    var resting = DockStripeGeometry.Resting(ContentWidth(count), area, scale);
                    var expanded = DockStripeGeometry.Expanded(ContentWidth(count), count, area, scale);

                    Assert.True(resting.Width > 0 && resting.Height > 0, $"degenerate resting stripe: {resting}");
                    Assert.True(expanded.Width >= resting.Width, "the expanded stripe must not be narrower");
                    Assert.Equal(resting.Height, expanded.Height);
                    Assert.True(expanded.Y >= area.Y, "the stripe must not be pushed off the top of the work area");
                }
            }
        }
    }
}
