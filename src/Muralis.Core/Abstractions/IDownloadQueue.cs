using System.Collections.ObjectModel;
using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Downloads wallpapers one at a time and retries failures, so several downloads can be
/// started from anywhere in the UI without each page running its own transfer. Items live
/// in memory only: quitting the app drops whatever is still waiting.
/// </summary>
public interface IDownloadQueue
{
    /// <summary>Live view of the queue, oldest first; the UI binds to it.</summary>
    ReadOnlyObservableCollection<DownloadItem> Items { get; }

    /// <summary>Raised whenever an item is added, removed, or changes state.</summary>
    event EventHandler? Changed;

    /// <summary>Number of items still waiting or transferring.</summary>
    int ActiveCount { get; }

    /// <summary>
    /// Queues a download of <paramref name="wallpaper"/> into <paramref name="targetDirectory"/>.
    /// An item for the same wallpaper is reused instead of stacking duplicates: a live one is
    /// returned as is, a failed or cancelled one is retried.
    /// </summary>
    DownloadItem Enqueue(Wallpaper wallpaper, string targetDirectory);

    /// <summary>The queue entry for a wallpaper, if it has one.</summary>
    DownloadItem? Find(string wallpaperId);

    /// <summary>Queues a failed or cancelled item again.</summary>
    void Retry(DownloadItem item);

    /// <summary>Aborts an item; a waiting one is marked cancelled immediately.</summary>
    void Cancel(DownloadItem item);

    /// <summary>Drops a finished item from the list.</summary>
    void Remove(DownloadItem item);

    /// <summary>Drops every completed, failed and cancelled item.</summary>
    void ClearFinished();
}
