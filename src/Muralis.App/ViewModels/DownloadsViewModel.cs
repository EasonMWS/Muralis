using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App.ViewModels;

/// <summary>
/// Shows the shared download queue: what is waiting, what is transferring, what failed
/// and what finished. Downloads keep running while the user is elsewhere in the app.
/// </summary>
public sealed partial class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadQueue _queue;
    private readonly INavigationService _navigation;
    private readonly IImageCacheService _imageCache;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    public DownloadsViewModel(
        IDownloadQueue queue,
        INavigationService navigation,
        IImageCacheService imageCache,
        ILocalizationService localization)
        : base(localization)
    {
        _queue = queue;
        _navigation = navigation;
        _imageCache = imageCache;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _queue.Changed += OnQueueChanged;
        SyncItems();
    }

    public ObservableCollection<DownloadRow> Items { get; } = [];

    public bool HasFinishedItems => Items.Any(row => !row.IsActive);

    [RelayCommand]
    private void ClearFinished()
    {
        _queue.ClearFinished();
        OnPropertyChanged(nameof(HasFinishedItems));
    }

    [RelayCommand]
    private void OpenBrowse() => _navigation.NavigateTo(Routes.Browse);

    public override void OnLanguageChanged()
    {
        RefreshSummary();
    }

    public override void DetachFromPage()
    {
        foreach (var row in Items)
        {
            row.Dispose();
        }

        _queue.Changed -= OnQueueChanged;
        base.DetachFromPage();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            SyncItems();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(SyncItems);
        }
    }

    private void SyncItems()
    {
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!_queue.Items.Contains(Items[index].Item))
            {
                Items[index].Dispose();
                Items.RemoveAt(index);
            }
        }

        foreach (var item in _queue.Items)
        {
            if (!Items.Any(row => ReferenceEquals(row.Item, item)))
            {
                Items.Add(new DownloadRow(item, _queue, Loc));
            }
        }

        // Thumbnails come from the same on-disk cache the grids use; asking for them here
        // keeps the list cheap even when it is opened long after the browsing session.
        if (Items.Count > 0)
        {
            _ = _imageCache.WarmThumbnailsAsync(
                Items.Select(row => row.Wallpaper).ToList(),
                PageToken);
        }

        IsEmpty = Items.Count == 0;
        OnPropertyChanged(nameof(HasFinishedItems));
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var active = _queue.ActiveCount;
        SummaryText = active > 0 ? Loc.Format("Downloads_Summary_Active", active) : Loc.Get("Downloads_Summary_Idle");
    }
}
