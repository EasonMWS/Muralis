using Muralis.Core.Models;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class RotationPlannerTests
{
    [Fact]
    public void PickNext_WithNoCandidates_ReturnsNull() =>
        Assert.Null(RotationPlanner.PickNext([], "local:a"));

    [Fact]
    public void PickNext_WithOnlyTheCurrentWallpaper_ReturnsIt()
    {
        var only = Create("local:a");

        var next = RotationPlanner.PickNext([only], "local:a");

        Assert.Same(only, next);
    }

    [Fact]
    public void PickNext_AvoidsTheCurrentWallpaper()
    {
        var candidates = new[] { Create("local:a"), Create("local:b"), Create("local:c") };
        var random = new Random(42);

        for (var i = 0; i < 30; i++)
        {
            var next = RotationPlanner.PickNext(candidates, "local:a", random);
            Assert.NotNull(next);
            Assert.NotEqual("local:a", next!.Id);
        }
    }

    [Fact]
    public void PickNext_IgnoresCandidatesWithoutLocalFiles()
    {
        var online = new Wallpaper { Id = "bing:x", Source = WallpaperSource.Online };
        var local = Create("local:a");

        var next = RotationPlanner.PickNext([online, local], null);

        Assert.Same(local, next);
    }

    [Fact]
    public void PickNext_WithOnlyOnlineCandidates_ReturnsNull() =>
        Assert.Null(RotationPlanner.PickNext([new Wallpaper { Id = "bing:x", Source = WallpaperSource.Online }], null));

    [Theory]
    [InlineData(RotationInterval.Minutes15, 15)]
    [InlineData(RotationInterval.Hours6, 360)]
    [InlineData(RotationInterval.Daily, 1440)]
    public void ToTimeSpan_MapsIntervalToMinutes(RotationInterval interval, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval.ToTimeSpan());

    private static Wallpaper Create(string id) => new() { Id = id, LocalPath = @"C:\pics\" + id.Replace(':', '_') + ".jpg" };
}
