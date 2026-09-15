using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class UpdateCheckerTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/example/muralis/releases/latest";

    [Fact]
    public async Task CheckAsync_WithNewerRelease_ReportsAvailable()
    {
        var (checker, handler) = CreateChecker(
            """{"tag_name":"v0.2.0","html_url":"https://example.test/releases/tag/v0.2.0"}""");

        var result = await checker.CheckAsync("0.1.0");

        Assert.NotNull(result);
        Assert.True(result!.IsUpdateAvailable);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal("https://example.test/releases/tag/v0.2.0", result.ReleaseUrl);
        Assert.Equal([ReleasesUrl], handler.RequestedUrls);
    }

    [Fact]
    public async Task CheckAsync_WithSameRelease_ReportsUpToDate()
    {
        var (checker, _) = CreateChecker("""{"tag_name":"0.1.0","html_url":"https://example.test/releases/tag/v0.1.0"}""");

        var result = await checker.CheckAsync("0.1.0");

        Assert.NotNull(result);
        Assert.False(result!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_WithOlderRelease_ReportsUpToDate()
    {
        var (checker, _) = CreateChecker("""{"tag_name":"v0.0.9"}""");

        var result = await checker.CheckAsync("0.1.0");

        Assert.NotNull(result);
        Assert.False(result!.IsUpdateAvailable);
        Assert.Null(result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_WithPrereleaseSuffix_ComparesTheNumericPart()
    {
        var (checker, _) = CreateChecker("""{"tag_name":"V0.2.0-beta.1+build.5"}""");

        var result = await checker.CheckAsync("0.1.0");

        Assert.NotNull(result);
        Assert.True(result!.IsUpdateAvailable);
        Assert.Equal("0.2.0-beta.1+build.5", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_WithUnreadableTag_ReturnsNull()
    {
        var (checker, _) = CreateChecker("""{"tag_name":"nightly","html_url":"https://example.test"}""");

        Assert.Null(await checker.CheckAsync("0.1.0"));
    }

    private static (UpdateChecker Checker, FakeHttpMessageHandler Handler) CreateChecker(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return (new UpdateChecker(httpClient, ReleasesUrl, NullLogger<UpdateChecker>.Instance), handler);
    }
}
