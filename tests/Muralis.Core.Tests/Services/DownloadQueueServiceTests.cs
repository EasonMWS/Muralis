using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;
using Muralis.Core.Services;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class DownloadQueueServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsService _settings;
    private readonly FakeLocalLibrary _library = new();
    private readonly FakeDownloadService _downloads = new();
    private readonly FakeWallpaperProvider _provider = new("wallhaven", "Wallhaven");
    private readonly WallpaperProviderManager _manager;

    public DownloadQueueServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-queue-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(_directory, "settings.json"));
        _settings.LoadAsync().GetAwaiter().GetResult();

        _manager = new WallpaperProviderManager(
            [_provider],
            _settings,
            new FakeProviderConfiguration(),
            _library,
            NullLogger<WallpaperProviderManager>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Enqueue_DownloadsAndRecordsTheFile()
    {
        var queue = CreateQueue();
        var wallpaper = Wallpaper("wallhaven:one");

        var item = queue.Enqueue(wallpaper, _directory);
        await WaitForStateAsync(item, DownloadState.Completed);

        Assert.Equal(1, item.Attempts);
        Assert.Equal(1d, item.Progress);
        Assert.NotNull(item.LocalPath);
        Assert.True(File.Exists(item.LocalPath));
        Assert.Equal(WallpaperSource.Online, wallpaper.Source);
        Assert.Equal("https://example.test/wallhaven:one", Assert.Single(_downloads.Urls));
        Assert.Equal(wallpaper.LocalPath, _library.Find(wallpaper.Id)?.LocalPath);
        Assert.False(queue.Items.Single().IsActive);
        Assert.False(item.IsDuplicate);
    }

    [Fact]
    public async Task Enqueue_MergesWithAnIdenticalFileAlreadyInTheLibrary()
    {
        // The same picture already sits in the library under a different name and id.
        var libraryDirectory = Path.Combine(_directory, "library");
        Directory.CreateDirectory(libraryDirectory);
        var existingPath = Path.Combine(libraryDirectory, "already-here.jpg");
        await File.WriteAllBytesAsync(existingPath, [1, 2, 3, 4]);
        var existing = Wallpaper("wallhaven:old");
        existing.LocalPath = existingPath;
        existing.ContentHash = await ContentHash.TryComputeAsync(existingPath);
        _library.Add(existing);

        var downloadsDirectory = Path.Combine(_directory, "downloads");
        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:new"), downloadsDirectory);
        await WaitForStateAsync(item, DownloadState.Completed);

        Assert.Equal(1, _downloads.Attempts);
        Assert.True(item.IsDuplicate);
        Assert.Equal(existingPath, item.LocalPath);
        Assert.Equal(existingPath, item.Wallpaper.LocalPath);
        Assert.Empty(Directory.GetFiles(downloadsDirectory));
        Assert.Equal(existingPath, _library.Find("wallhaven:new")?.LocalPath);
    }

    [Fact]
    public async Task Enqueue_RedownloadingOverTheSameFile_KeepsTheOnlyCopy()
    {
        var queue = CreateQueue();
        var wallpaper = Wallpaper("wallhaven:one");
        var first = queue.Enqueue(wallpaper, _directory);
        await WaitForStateAsync(first, DownloadState.Completed);
        var path = first.LocalPath!;

        // The user clears the finished row and asks for the same wallpaper again.
        queue.Remove(first);
        var second = queue.Enqueue(wallpaper, _directory);
        await WaitForStateAsync(second, DownloadState.Completed);

        Assert.False(second.IsDuplicate);
        Assert.Equal(path, second.LocalPath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Enqueue_SameWallpaperTwice_ReusesTheItem()
    {
        var gate = new TaskCompletionSource<string>();
        _downloads.OnDownload = (_, _) => gate.Task;
        var queue = CreateQueue();
        var wallpaper = Wallpaper("wallhaven:one");

        var first = queue.Enqueue(wallpaper, _directory);
        var second = queue.Enqueue(wallpaper, _directory);
        gate.SetResult(_downloads.CreateCompletedFile());
        await WaitForStateAsync(first, DownloadState.Completed);

        Assert.Same(first, second);
        Assert.Single(queue.Items);
        Assert.Equal(1, _downloads.Attempts);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedAndSucceeds()
    {
        _downloads.OnDownload = (attempt, _) => attempt == 1
            ? _downloads.FailAsync()
            : _downloads.SucceedAsync();

        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(item, DownloadState.Completed);

        Assert.Equal(2, item.Attempts);
        Assert.Null(item.FailureReason);
        Assert.Equal(2, _downloads.Attempts);
    }

    [Fact]
    public async Task PersistentFailure_StopsAtMaxAttempts()
    {
        _downloads.OnDownload = (_, _) => _downloads.FailAsync();

        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(item, DownloadState.Failed);

        Assert.Equal(item.MaxAttempts, item.Attempts);
        Assert.Equal(3, _downloads.Attempts);
        Assert.Equal("offline", item.FailureReason);
        Assert.True(queue.Items.Single().State == DownloadState.Failed);
    }

    [Fact]
    public async Task Retry_AfterAFinalFailure_StartsANewRound()
    {
        _downloads.OnDownload = (_, _) => _downloads.FailAsync();

        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(item, DownloadState.Failed);

        _downloads.OnDownload = (_, _) => _downloads.SucceedAsync();
        queue.Retry(item);
        await WaitForStateAsync(item, DownloadState.Completed);

        Assert.Equal(1, item.Attempts);
        Assert.Null(item.FailureReason);
    }

    [Fact]
    public async Task Enqueue_AFailedItem_RetriesItInsteadOfAddingADuplicate()
    {
        _downloads.OnDownload = (_, _) => _downloads.FailAsync();

        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(item, DownloadState.Failed);

        _downloads.OnDownload = (_, _) => _downloads.SucceedAsync();
        var requeued = queue.Enqueue(item.Wallpaper, _directory);
        await WaitForStateAsync(requeued, DownloadState.Completed);

        Assert.Same(item, requeued);
        Assert.Single(queue.Items);
    }

    [Fact]
    public async Task Cancel_WhileQueued_SkipsTheTransfer()
    {
        var gate = new TaskCompletionSource<string>();
        _downloads.OnDownload = (_, _) => gate.Task;

        var queue = CreateQueue();
        var active = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        var waiting = queue.Enqueue(Wallpaper("wallhaven:two"), _directory);
        await WaitForStateAsync(active, DownloadState.Downloading);

        queue.Cancel(waiting);
        gate.SetResult(_downloads.CreateCompletedFile());
        await WaitForStateAsync(active, DownloadState.Completed);
        await WaitForStateAsync(waiting, DownloadState.Cancelled);

        Assert.Equal(1, _downloads.Attempts);
        Assert.Single(_downloads.Urls);
    }

    [Fact]
    public async Task Cancel_WhileDownloading_AbortsTheTransfer()
    {
        _downloads.OnDownload = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        };

        var queue = CreateQueue();
        var item = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(item, DownloadState.Downloading);

        queue.Cancel(item);
        await WaitForStateAsync(item, DownloadState.Cancelled);

        Assert.Equal(1, _downloads.Attempts);
    }

    [Fact]
    public async Task ClearFinished_RemovesOnlyFinishedItems()
    {
        _downloads.OnDownload = (_, _) => _downloads.SucceedAsync();
        var queue = CreateQueue();
        var done = queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        await WaitForStateAsync(done, DownloadState.Completed);

        _downloads.OnDownload = (_, _) => _downloads.FailAsync();
        var failed = queue.Enqueue(Wallpaper("wallhaven:two"), _directory);
        await WaitForStateAsync(failed, DownloadState.Failed);

        queue.ClearFinished();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.ActiveCount);
    }

    [Fact]
    public async Task ActiveCount_TracksWaitingAndTransferringItems()
    {
        var gate = new TaskCompletionSource<string>();
        _downloads.OnDownload = (_, _) => gate.Task;
        var queue = CreateQueue();

        queue.Enqueue(Wallpaper("wallhaven:one"), _directory);
        queue.Enqueue(Wallpaper("wallhaven:two"), _directory);
        Assert.Equal(2, queue.ActiveCount);

        gate.SetResult(_downloads.CreateCompletedFile());
        await WaitForStateAsync(queue.Items[1], DownloadState.Completed);

        Assert.Equal(0, queue.ActiveCount);
    }

    [Fact]
    public async Task RealDownloadService_WritesTheFileAndUpdatesTheWallpaper()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var http = new HttpClient(FakeHttpMessageHandler.Bytes(payload));
        var queue = new DownloadQueueService(
            new DownloadService(http, NullLogger<DownloadService>.Instance),
            _library,
            _manager,
            NullLogger<DownloadQueueService>.Instance,
            TimeSpan.Zero);

        var wallpaper = Wallpaper("wallhaven:one");
        var item = queue.Enqueue(wallpaper, Path.Combine(_directory, "downloads"));
        await WaitForStateAsync(item, DownloadState.Completed);

        Assert.NotNull(item.LocalPath);
        Assert.True(File.Exists(item.LocalPath));
        Assert.Equal(payload.Length, new FileInfo(item.LocalPath!).Length);
        Assert.Equal(item.LocalPath, wallpaper.LocalPath);
        Assert.Equal(payload.Length, wallpaper.FileSize);
        Assert.Equal(WallpaperSource.Online, wallpaper.Source);
    }

    private DownloadQueueService CreateQueue() =>
        new(
            _downloads,
            _library,
            _manager,
            NullLogger<DownloadQueueService>.Instance,
            retryDelay: TimeSpan.Zero);

    private static Wallpaper Wallpaper(string id) => new()
    {
        Id = id,
        Title = id,
        RemoteUrl = "https://example.test/" + id,
        Source = WallpaperSource.Online,
    };

    private static async Task WaitForStateAsync(DownloadItem item, DownloadState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (item.State != state)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"'{item.Title}' stayed in {item.State} instead of reaching {state}.");
            }

            await Task.Delay(20);
        }
    }
}
