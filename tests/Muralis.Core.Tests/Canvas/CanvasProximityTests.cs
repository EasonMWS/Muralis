using Muralis.Core.Canvas;
using Xunit;

namespace Muralis.Core.Tests.Canvas;

public sealed class CanvasProximityTests
{
    private static readonly CanvasProximityOptions Options = new()
    {
        MaxScale = 1.6,
        InfluenceRadiusDip = 280,
        Falloff = ProximityFalloff.Smoothstep,
    };

    [Theory]
    [InlineData(ProximityFalloff.Smoothstep)]
    [InlineData(ProximityFalloff.Gaussian)]
    public void CursorOnTheItem_ScalesToMax(ProximityFalloff falloff)
    {
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 280, Falloff = falloff };

        Assert.Equal(1.6, CanvasProximity.ScaleAt(0, options), precision: 9);
    }

    [Theory]
    [InlineData(ProximityFalloff.Smoothstep)]
    [InlineData(ProximityFalloff.Gaussian)]
    public void AtAndBeyondTheRadius_NothingScales(ProximityFalloff falloff)
    {
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 280, Falloff = falloff };

        Assert.Equal(1.0, CanvasProximity.ScaleAt(280, options), precision: 9);
        Assert.Equal(1.0, CanvasProximity.ScaleAt(600, options), precision: 9);
    }

    [Theory]
    [InlineData(ProximityFalloff.Smoothstep)]
    [InlineData(ProximityFalloff.Gaussian)]
    public void ScaleNeverIncreasesWithDistance(ProximityFalloff falloff)
    {
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 280, Falloff = falloff };
        var previous = double.MaxValue;

        for (var distance = 0.0; distance <= 420; distance += 4)
        {
            var scale = CanvasProximity.ScaleAt(distance, options);
            Assert.True(scale <= previous + 1e-9, $"scale rose between samples at distance {distance}");
            Assert.InRange(scale, 1.0, 1.6);
            previous = scale;
        }
    }

    [Fact]
    public void Smoothstep_HalfwayOut_IsHalfTheGrowth()
    {
        // t = 0.5 gives smoothstep 0.5, so half the radius means half of the extra scale.
        var scale = CanvasProximity.ScaleAt(Options.InfluenceRadiusDip / 2, Options);

        Assert.Equal(1.3, scale, precision: 9);
    }

    [Theory]
    [InlineData(ProximityFalloff.Smoothstep)]
    [InlineData(ProximityFalloff.Gaussian)]
    public void CurveIsContinuousAtTheRadius(ProximityFalloff falloff)
    {
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 280, Falloff = falloff };

        var justInside = CanvasProximity.ScaleAt(279.999, options);
        Assert.True(Math.Abs(justInside - 1.0) < 1e-4, $"expected an almost settled scale, got {justInside}");
    }

    [Fact]
    public void Gaussian_FadesFasterNearTheRimThanSmoothstep()
    {
        var smoothstep = new CanvasProximityOptions { Falloff = ProximityFalloff.Smoothstep };
        var gaussian = new CanvasProximityOptions { Falloff = ProximityFalloff.Gaussian };
        var atThreeQuarters = smoothstep.InfluenceRadiusDip * 0.75;

        Assert.True(
            CanvasProximity.ScaleAt(atThreeQuarters, gaussian) < CanvasProximity.ScaleAt(atThreeQuarters, smoothstep));
    }

    [Fact]
    public void ScalingDisabled_ReturnsOne()
    {
        var flat = new CanvasProximityOptions { MaxScale = 1.0 };
        var noRadius = new CanvasProximityOptions { InfluenceRadiusDip = 0 };

        Assert.Equal(1.0, CanvasProximity.ScaleAt(0, flat));
        Assert.Equal(1.0, CanvasProximity.ScaleAt(0, noRadius));
    }

    [Fact]
    public void ScaleForItem_MeasuresEuclideanDistanceToTheCentre()
    {
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 100 };

        var onCentre = CanvasProximity.ScaleForItem(500, 500, 500, 500, options);
        var offsetDiagonally = CanvasProximity.ScaleForItem(560, 580, 500, 500, options);
        var straightDistance = CanvasProximity.ScaleAt(100, options);

        Assert.Equal(1.6, onCentre, precision: 9);
        Assert.Equal(straightDistance, offsetDiagonally, precision: 9);
    }

    [Fact]
    public void AGroupOfItems_ShowsTheMagnificationBell()
    {
        // Four items in a row with the cursor just left of the second one: each item grows less
        // than the one before, and the far end of the row is not touched at all.
        var options = new CanvasProximityOptions { MaxScale = 1.6, InfluenceRadiusDip = 280 };
        double[] offsets = [-255, -85, 85, 255];
        var scales = offsets
            .Select(offset => CanvasProximity.ScaleForItem(-120, 0, offset, 0, options))
            .ToList();

        Assert.Equal(1.0, scales[3], precision: 9);
        Assert.True(scales[0] > scales[2] && scales[2] > 1.0);
        Assert.True(scales[1] > scales[0] && scales[1] < 1.6);
    }
}
