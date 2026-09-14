using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Providers;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Providers;

public sealed class ApodWallpaperProviderTests
{
    private const string FeaturedJson = """
        {
          "date": "2026-09-14",
          "explanation": "The hydrogen in your body...",
          "hdurl": "https://apod.nasa.gov/apod/image/2609/Nebula_4096.jpg",
          "media_type": "image",
          "service_version": "v1",
          "title": "A Fancy Nebula",
          "url": "https://apod.nasa.gov/apod/image/2609/Nebula_1024.jpg",
          "copyright": "Jane Doe"
        }
        """;

    private const string CollectionJson = """
        [
          {
            "date": "2026-09-14",
            "title": "A Fancy Nebula",
            "media_type": "image",
            "hdurl": "https://apod.nasa.gov/apod/image/2609/Nebula_4096.jpg",
            "url": "https://apod.nasa.gov/apod/image/2609/Nebula_1024.jpg"
          },
          {
            "date": "2026-09-13",
            "title": "A Video Night",
            "media_type": "video",
            "url": "https://www.youtube.com/embed/abc123"
          },
          {
            "date": "2026-09-12",
            "title": "No HD Version",
            "media_type": "image",
            "url": "https://apod.nasa.gov/apod/image/2609/Small_1024.png"
          }
        ]
        """;

    [Fact]
    public void RequiresAnApiKey() => Assert.True(CreateProvider(FeaturedJson).RequiresApiKey);

    [Fact]
    public void SearchIsNotSupported() => Assert.False(CreateProvider(FeaturedJson).SupportsSearch);

    [Fact]
    public async Task GetFeaturedAsync_MapsTodaysImage()
    {
        var provider = CreateProvider(FeaturedJson);

        var items = await provider.GetFeaturedAsync(1);

        var wallpaper = Assert.Single(items);
        Assert.Equal("nasa:2026-09-14", wallpaper.Id);
        Assert.Equal("A Fancy Nebula", wallpaper.Title);
        Assert.Equal("https://apod.nasa.gov/apod/image/2609/Nebula_4096.jpg", wallpaper.RemoteUrl);
        Assert.Equal("https://apod.nasa.gov/apod/image/2609/Nebula_1024.jpg", wallpaper.ThumbnailUrl);
        Assert.Equal(WallpaperSource.Online, wallpaper.Source);
        Assert.Contains("NASA", wallpaper.Tags);
        Assert.Contains("Jane Doe", wallpaper.Tags);
    }

    [Fact]
    public async Task GetWallpapersAsync_SkipsVideosAndFallsBackToTheStandardImage()
    {
        var provider = CreateProvider(CollectionJson);

        var items = await provider.GetWallpapersAsync(new WallpaperQuery { PageSize = 10 });

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.Title == "A Video Night");
        Assert.Equal("https://apod.nasa.gov/apod/image/2609/Small_1024.png", items[1].RemoteUrl);
    }

    [Fact]
    public async Task Requests_CarryTheApiKey()
    {
        var handler = FakeHttpMessageHandler.Json(FeaturedJson);
        var provider = CreateProvider(handler, new FakeProviderConfiguration().WithKey("nasa", "my-key"));

        await provider.GetFeaturedAsync(1);

        Assert.Contains("api_key=my-key", handler.RequestedUrls.Single());
    }

    [Fact]
    public async Task GetWallpaperAsync_LooksUpByDate()
    {
        var handler = FakeHttpMessageHandler.Json(FeaturedJson);
        var provider = CreateProvider(handler);

        var wallpaper = await provider.GetWallpaperAsync("nasa:2026-09-14");

        Assert.NotNull(wallpaper);
        Assert.Contains("date=2026-09-14", handler.RequestedUrls.Single());
    }

    [Fact]
    public async Task GetWallpaperAsync_WithUnusableId_ReturnsNull()
    {
        var provider = CreateProvider(FeaturedJson);

        Assert.Null(await provider.GetWallpaperAsync("nasa:not-a-date"));
    }

    private static ApodWallpaperProvider CreateProvider(string json) =>
        CreateProvider(FakeHttpMessageHandler.Json(json));

    private static ApodWallpaperProvider CreateProvider(
        FakeHttpMessageHandler handler,
        IProviderConfiguration? configuration = null)
    {
        var client = new HttpClient(handler);
        return new ApodWallpaperProvider(
            client,
            configuration ?? new FakeProviderConfiguration(),
            NullLogger<ApodWallpaperProvider>.Instance);
    }
}
