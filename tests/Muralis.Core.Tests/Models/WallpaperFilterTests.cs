using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Models;

public sealed class WallpaperFilterTests
{
    [Fact]
    public void IsActive_IsFalseForAnyAny()
    {
        Assert.False(WallpaperFilter.IsActive(WallpaperOrientation.Any, WallpaperResolution.Any));
    }

    [Theory]
    [InlineData(WallpaperOrientation.Landscape, WallpaperResolution.Any)]
    [InlineData(WallpaperOrientation.Any, WallpaperResolution.QuadHd)]
    public void IsActive_IsTrueWhenEitherFilterIsSet(WallpaperOrientation orientation, WallpaperResolution resolution)
    {
        Assert.True(WallpaperFilter.IsActive(orientation, resolution));
    }

    [Fact]
    public void Matches_KeepsWallpapersWithUnknownDimensions()
    {
        var wallpaper = new Wallpaper { Id = "x", Width = 0, Height = 0 };

        Assert.True(WallpaperFilter.Matches(wallpaper, WallpaperOrientation.Portrait, WallpaperResolution.UltraHd));
    }

    [Theory]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, false)]
    [InlineData(1920, 1920, true)] // square counts as landscape
    public void Matches_AppliesOrientation(int width, int height, bool expected)
    {
        var wallpaper = new Wallpaper { Id = "x", Width = width, Height = height };

        Assert.Equal(expected, WallpaperFilter.Matches(wallpaper, WallpaperOrientation.Landscape, WallpaperResolution.Any));
    }

    [Theory]
    [InlineData(3840, 2160, WallpaperResolution.UltraHd, true)]
    [InlineData(3840, 2160, WallpaperResolution.QuadHd, true)]
    [InlineData(2560, 1440, WallpaperResolution.UltraHd, false)]
    [InlineData(2560, 1440, WallpaperResolution.QuadHd, true)]
    [InlineData(1920, 1080, WallpaperResolution.FullHd, true)]
    [InlineData(1600, 900, WallpaperResolution.FullHd, false)]
    [InlineData(2160, 3840, WallpaperResolution.UltraHd, true)] // portrait 4K: long edge counts
    public void Matches_AppliesMinimumResolution(int width, int height, WallpaperResolution resolution, bool expected)
    {
        var wallpaper = new Wallpaper { Id = "x", Width = width, Height = height };

        Assert.Equal(expected, WallpaperFilter.Matches(wallpaper, WallpaperOrientation.Any, resolution));
    }

    [Fact]
    public void MinimumSize_MapsResolutionClasses()
    {
        Assert.Equal((1920, 1080), WallpaperFilter.MinimumSize(WallpaperResolution.FullHd));
        Assert.Equal((2560, 1440), WallpaperFilter.MinimumSize(WallpaperResolution.QuadHd));
        Assert.Equal((3840, 2160), WallpaperFilter.MinimumSize(WallpaperResolution.UltraHd));
        Assert.Equal((0, 0), WallpaperFilter.MinimumSize(WallpaperResolution.Any));
    }
}
