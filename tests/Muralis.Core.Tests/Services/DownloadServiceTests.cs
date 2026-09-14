using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class DownloadServiceTests : IDisposable
{
    private readonly string _directory;

    public DownloadServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Download_WritesFileWithSanitizedName()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = CreateService(FakeHttpMessageHandler.Bytes(payload));

        var path = await service.DownloadAsync("https://example.com/photos/sunset.jpg", _directory, "Sunset: over? the sea");

        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal("Sunset_ over_ the sea.jpg", Path.GetFileName(path));
    }

    [Fact]
    public async Task Download_WithExistingFile_AddsNumericSuffix()
    {
        var service = CreateService(FakeHttpMessageHandler.Bytes([9, 9, 9]));
        var first = await service.DownloadAsync("https://example.com/a.jpg", _directory, "wallpaper");

        var second = await service.DownloadAsync("https://example.com/a.jpg", _directory, "wallpaper");

        Assert.Equal("wallpaper.jpg", Path.GetFileName(first));
        Assert.Equal("wallpaper (2).jpg", Path.GetFileName(second));
    }

    [Fact]
    public async Task Download_ReportsProgressUpToOne()
    {
        var service = CreateService(FakeHttpMessageHandler.Bytes(new byte[10_000]));
        var reports = new List<double>();
        var progress = new Progress<double>(reports.Add);

        await service.DownloadAsync("https://example.com/big.jpg", _directory, "big", progress);

        Assert.NotEmpty(reports);
        Assert.Equal(1d, reports[^1], precision: 3);
    }

    [Fact]
    public async Task Download_KeepsExtensionFromUrl()
    {
        var service = CreateService(FakeHttpMessageHandler.Bytes([1]));

        var path = await service.DownloadAsync("https://example.com/image.png", _directory, "shot");

        Assert.EndsWith(".png", path);
    }

    [Fact]
    public async Task Download_OnHttpError_ThrowsAndLeavesNoFile()
    {
        var service = CreateService(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.DownloadAsync("https://example.com/missing.jpg", _directory, "missing"));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Download_Cancellation_LeavesNoFile()
    {
        var service = CreateService(FakeHttpMessageHandler.Bytes(new byte[1024]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync("https://example.com/x.jpg", _directory, "x", null, cts.Token));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    private static DownloadService CreateService(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<DownloadService>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
