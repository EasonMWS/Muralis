using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class ImageCacheServiceTests : IDisposable
{
    private readonly string _directory;

    public ImageCacheServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-thumb-tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Warm_DownloadsThumbnailAndAssignsPath()
    {
        var handler = FakeHttpMessageHandler.Bytes([1, 2, 3]);
        var service = CreateService(handler);
        var wallpaper = CreateOnlineWallpaper();

        await service.WarmThumbnailsAsync([wallpaper]);

        Assert.NotNull(wallpaper.CachedThumbnailPath);
        Assert.True(File.Exists(wallpaper.CachedThumbnailPath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Warm_SecondCall_UsesDiskCache()
    {
        var handler = FakeHttpMessageHandler.Bytes([1, 2, 3]);
        var service = CreateService(handler);

        await service.WarmThumbnailsAsync([CreateOnlineWallpaper()]);
        await service.WarmThumbnailsAsync([CreateOnlineWallpaper()]);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Warm_SkipsLocalAndThumbnailLessEntries()
    {
        var handler = FakeHttpMessageHandler.Bytes([1]);
        var service = CreateService(handler);
        var local = new Wallpaper { Id = "local:x", LocalPath = @"C:\pics\x.jpg" };
        var noThumbnail = new Wallpaper { Id = "bing:y", Source = WallpaperSource.Online };

        await service.WarmThumbnailsAsync([local, noThumbnail]);

        Assert.Equal(0, handler.RequestCount);
        Assert.Null(local.CachedThumbnailPath);
        Assert.Null(noThumbnail.CachedThumbnailPath);
    }

    [Fact]
    public async Task Warm_NetworkFailure_IsSwallowed()
    {
        var service = CreateService(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.Forbidden));
        var wallpaper = CreateOnlineWallpaper();

        await service.WarmThumbnailsAsync([wallpaper]);

        Assert.Null(wallpaper.CachedThumbnailPath);
    }

    private ImageCacheService CreateService(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<ImageCacheService>.Instance, _directory);

    private static Wallpaper CreateOnlineWallpaper() => new()
    {
        Id = "bing:20260913",
        Title = "Daily image",
        ThumbnailUrl = "https://www.bing.com/th?id=OHR.Test_1920x1080.jpg",
        Source = WallpaperSource.Online,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
