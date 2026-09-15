using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Providers;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Providers;

public sealed class BingWallpaperProviderTests
{
    private const string SampleJson = """
        {
          "images": [
            {
              "startdate": "20260913",
              "url": "/th?id=OHR.KochiaChina_ZH-CN4719995421_1920x1080.jpg&rf=LaDigue_1920x1080.jpg&pid=hp",
              "urlbase": "/th?id=OHR.KochiaChina_ZH-CN4719995421",
              "copyright": "地肤田，中国 (© lingqi xie/Getty Images)",
              "title": "坚韧在此扎根"
            },
            {
              "startdate": "20260912",
              "url": "/th?id=OHR.MisurinaPeak_ZH-CN3877105161_1920x1080.jpg&pid=hp",
              "urlbase": "/th?id=OHR.MisurinaPeak_ZH-CN3877105161",
              "copyright": "米苏里纳群峰，多洛米蒂山脉 (© Vithun Khamsong/Getty Images)",
              "title": ""
            }
          ]
        }
        """;

    [Fact]
    public async Task GetWallpapersAsync_MapsImagesFromTheFeed()
    {
        var provider = CreateProvider(FakeHttpMessageHandler.Json(SampleJson));

        var items = await provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery());

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("bing:20260913", first.Id);
        Assert.Equal("坚韧在此扎根", first.Title);
        Assert.Equal("https://www.bing.com/th?id=OHR.KochiaChina_ZH-CN4719995421_UHD.jpg", first.RemoteUrl);
        Assert.Equal("https://www.bing.com/th?id=OHR.KochiaChina_ZH-CN4719995421_1920x1080.jpg", first.ThumbnailUrl);
        Assert.Equal(Muralis.Core.Models.WallpaperSource.Online, first.Source);
        Assert.Contains("Bing", first.Tags);
    }

    [Fact]
    public void CategoriesAreNotSupported() =>
        Assert.False(((IWallpaperProvider)CreateProvider(FakeHttpMessageHandler.Json(SampleJson))).SupportsCategories);

    [Fact]
    public async Task GetWallpapersAsync_FallsBackToCopyrightWhenTitleIsMissing()
    {
        var provider = CreateProvider(FakeHttpMessageHandler.Json(SampleJson));

        var items = await provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery());

        Assert.Equal("米苏里纳群峰，多洛米蒂山脉", items[1].Title);
    }

    [Fact]
    public async Task GetWallpaperAsync_FindsById()
    {
        var provider = CreateProvider(FakeHttpMessageHandler.Json(SampleJson));

        var item = await provider.GetWallpaperAsync("bing:20260912");
        var missing = await provider.GetWallpaperAsync("bing:19700101");

        Assert.NotNull(item);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Feed_IsFetchedOnlyOnceWithinItsLifetime()
    {
        var handler = FakeHttpMessageHandler.Json(SampleJson);
        var provider = CreateProvider(handler);

        await provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery());
        await provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery());

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EmptyFeed_Throws()
    {
        var provider = CreateProvider(FakeHttpMessageHandler.Json("""{ "images": [] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery()));
    }

    [Fact]
    public async Task HttpFailure_SurfacesAsRequestException()
    {
        var provider = CreateProvider(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetWallpapersAsync(new Muralis.Core.Abstractions.WallpaperQuery()));
    }

    private static BingWallpaperProvider CreateProvider(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<BingWallpaperProvider>.Instance);
}
