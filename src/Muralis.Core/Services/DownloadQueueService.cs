using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// Runs one download at a time on the calling (UI) thread so the wallpapers it updates stay
/// safe to bind to. Failures are retried automatically with a growing delay before the item
/// is handed to the user as failed.
/// </summary>
public sealed class DownloadQueueService : IDownloadQueue
{
    private readonly IDownloadService _downloads;
    private readonly ILocalLibrary _library;
    private readonly WallpaperProviderManager _providers;
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly TimeSpan _retryDelay;
    private readonly ObservableCollection<DownloadItem> _items = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task _worker = Task.CompletedTask;
    private bool _isWorkerRunning;

    public DownloadQueueService(
        IDownloadService downloads,
        ILocalLibrary library,
        WallpaperProviderManager providers,
        ILogger<DownloadQueueService> logger,
        TimeSpan? retryDelay = null)
    {
        _downloads = downloads;
        _library = library;
        _providers = providers;
        _logger = logger;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
        Items = new ReadOnlyObservableCollection<DownloadItem>(_items);
    }

    public ReadOnlyObservableCollection<DownloadItem> Items { get; }

    public event EventHandler? Changed;

    public int ActiveCount => _items.Count(item => item.IsActive);

    public DownloadItem Enqueue(Wallpaper wallpaper, string targetDirectory)
    {
        if (Find(wallpaper.Id) is { } existing)
        {
            if (!existing.IsActive && existing.State != DownloadState.Completed)
            {
                _logger.LogInformation("Requeueing '{Title}' ({Id})", existing.Title, existing.WallpaperId);
                Retry(existing);
            }

            return existing;
        }

        var item = new DownloadItem(wallpaper, targetDirectory);
        _items.Add(item);
        _logger.LogInformation("Queued download of '{Title}' ({Id})", item.Title, item.WallpaperId);
        RaiseChanged();
        EnsureWorkerRunning();
        return item;
    }

    public DownloadItem? Find(string wallpaperId) =>
        _items.FirstOrDefault(item => string.Equals(item.WallpaperId, wallpaperId, StringComparison.Ordinal));

    public void Retry(DownloadItem item)
    {
        if (!_items.Contains(item) || item.IsActive)
        {
            return;
        }

        item.PrepareForRetry();
        RaiseChanged();
        EnsureWorkerRunning();
    }

    public void Cancel(DownloadItem item)
    {
        if (!_items.Contains(item))
        {
            return;
        }

        item.RequestCancel();
        if (item.State == DownloadState.Queued)
        {
            item.State = DownloadState.Cancelled;
        }

        RaiseChanged();
    }

    public void Remove(DownloadItem item)
    {
        if (!_items.Remove(item))
        {
            return;
        }

        item.RequestCancel();
        RaiseChanged();
    }

    public void ClearFinished()
    {
        var finished = _items.Where(item => !item.IsActive).ToList();
        if (finished.Count == 0)
        {
            return;
        }

        foreach (var item in finished)
        {
            _items.Remove(item);
        }

        RaiseChanged();
    }

    private void EnsureWorkerRunning()
    {
        if (_isWorkerRunning || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _isWorkerRunning = true;
        _worker = RunAsync(_shutdown.Token);
    }

    private async Task RunAsync(CancellationToken shutdownToken)
    {
        try
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                var item = _items.FirstOrDefault(candidate => candidate.State == DownloadState.Queued);
                if (item is null)
                {
                    return;
                }

                await ProcessAsync(item, shutdownToken).ConfigureAwait(true);
            }
        }
        finally
        {
            _isWorkerRunning = false;

            // An item queued while the loop was winding down still needs a worker.
            if (!shutdownToken.IsCancellationRequested
                && _items.Any(candidate => candidate.State == DownloadState.Queued))
            {
                EnsureWorkerRunning();
            }
        }
    }

    private async Task ProcessAsync(DownloadItem item, CancellationToken shutdownToken)
    {
        while (item.State == DownloadState.Queued && !shutdownToken.IsCancellationRequested)
        {
            item.Attempts++;
            item.State = DownloadState.Downloading;
            item.Progress = 0;
            RaiseChanged();

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(item.Cancellation.Token, shutdownToken);
            var token = attempt.Token;
            var progress = new Progress<double>(value => item.Progress = value);

            try
            {
                var sourceUrl = await _providers.GetDownloadUrlAsync(item.Wallpaper, token).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(sourceUrl))
                {
                    throw new InvalidOperationException("The wallpaper has no download link.");
                }

                var path = await _downloads
                    .DownloadAsync(sourceUrl, item.TargetDirectory, item.Title, progress, token)
                    .ConfigureAwait(true);

                await CompleteAsync(item, path).ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException)
            {
                // Either the user cancelled this item or the app is closing.
                item.State = item.Cancellation.IsCancellationRequested
                    ? DownloadState.Cancelled
                    : DownloadState.Queued;
                RaiseChanged();
                return;
            }
            catch (Exception ex)
            {
                item.FailureReason = ex.Message;
                _logger.LogWarning(
                    ex,
                    "Download attempt {Attempt} of {Max} failed for '{Title}'",
                    item.Attempts,
                    item.MaxAttempts,
                    item.Title);

                if (item.Attempts >= item.MaxAttempts || shutdownToken.IsCancellationRequested)
                {
                    item.State = DownloadState.Failed;
                    RaiseChanged();
                    return;
                }

                item.State = DownloadState.Queued;
                RaiseChanged();
                try
                {
                    await Task.Delay(_retryDelay * item.Attempts, item.Cancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    item.State = DownloadState.Cancelled;
                    RaiseChanged();
                    return;
                }
            }
        }
    }

    private async Task CompleteAsync(DownloadItem item, string path)
    {
        var wallpaper = item.Wallpaper;
        if (ImageMetadataReader.TryReadDimensions(path, out var width, out var height))
        {
            wallpaper.Width = width;
            wallpaper.Height = height;
        }

        wallpaper.FileSize = new FileInfo(path).Length;
        wallpaper.LocalPath = path;
        await _library.SaveAsync(wallpaper).ConfigureAwait(true);

        _logger.LogInformation("Downloaded '{Title}' to {Path}", wallpaper.Title, path);

        item.LocalPath = path;
        item.Progress = 1;
        item.FailureReason = null;
        item.State = DownloadState.Completed;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
