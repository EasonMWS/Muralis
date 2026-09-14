using Muralis.Core.Helpers;
using Xunit;

namespace Muralis.Core.Tests.Helpers;

public sealed class WallpaperIdTests
{
    [Fact]
    public void ForLocalFile_IsStableAcrossCasingAndSeparators() =>
        Assert.Equal(
            WallpaperId.ForLocalFile(@"C:\Users\Eason\Pictures\Sunset.JPG"),
            WallpaperId.ForLocalFile("c:/users/eason/pictures/sunset.jpg"));

    [Fact]
    public void ForLocalFile_DiffersForDifferentPaths() =>
        Assert.NotEqual(
            WallpaperId.ForLocalFile(@"C:\Pictures\a.jpg"),
            WallpaperId.ForLocalFile(@"C:\Pictures\b.jpg"));

    [Fact]
    public void ForRemote_UsesProviderPrefix()
    {
        var id = WallpaperId.ForRemote("bing", "2026-09-14");

        Assert.Equal("bing:2026-09-14", id);
    }
}
