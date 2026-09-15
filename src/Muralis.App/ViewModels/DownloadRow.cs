using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>
/// One line of the download list: wraps a queue item and turns its state into the
/// localized text and the small set of actions that make sense in that state.
/// </summary>
public sealed partial class DownloadRow : ViewModelBase
{
    private readonly IDownloadQueue _queue;

    public DownloadRow(DownloadItem item, IDownloadQueue queue, ILocalizationService localization)
        : base(localization)
    {
        Item = item;
        _queue = queue;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    public DownloadItem Item { get; }

    public Wallpaper Wallpaper => Item.Wallpaper;

    public string Title => Item.Title;

    public string StatusText => Item.State switch
    {
        DownloadState.Queued => Loc.Get("Downloads_State_Queued"),
        DownloadState.Downloading => Loc.Format("Downloads_State_Downloading", ProgressPercent),
        DownloadState.Completed => Loc.Get("Downloads_State_Completed"),
        DownloadState.Cancelled => Loc.Get("Downloads_State_Cancelled"),
        _ => Loc.Get("Downloads_State_Failed"),
    };

    public string DetailText => Item.State switch
    {
        DownloadState.Failed when !string.IsNullOrWhiteSpace(Item.FailureReason) =>
            Loc.Format("Downloads_State_FailedReason", Item.FailureReason),
        DownloadState.Cancelled => Loc.Get("Downloads_State_CancelledHint"),
        DownloadState.Completed => Item.LocalPath ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>Fraction for the progress bar; bound once per state change and per progress report.</summary>
    public double Progress => Item.Progress;

    public double ProgressPercent => Math.Round(Item.Progress * 100);

    public bool IsActive => Item.IsActive;

    public bool IsDownloading => Item.State == DownloadState.Downloading;

    public bool CanRetry => Item.State is DownloadState.Failed or DownloadState.Cancelled;

    public bool CanOpenInExplorer => Item.State == DownloadState.Completed && Item.LocalPath is { } path && File.Exists(path);

    /// <summary>Raised when the row is taken out of the list, so it stops listening to the item.</summary>
    public void Dispose() => Item.PropertyChanged -= OnItemPropertyChanged;

    public override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
    }

    [RelayCommand]
    private void Cancel() => _queue.Cancel(Item);

    [RelayCommand]
    private void Retry() => _queue.Retry(Item);

    [RelayCommand]
    private void Remove() => _queue.Remove(Item);

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (Item.LocalPath is not { } path || !File.Exists(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DownloadItem.Progress):
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(DownloadItem.State):
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanOpenInExplorer));
                break;
            case nameof(DownloadItem.LocalPath):
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(CanOpenInExplorer));
                break;
            case nameof(DownloadItem.FailureReason):
                OnPropertyChanged(nameof(DetailText));
                break;
            case nameof(DownloadItem.Attempts):
                OnPropertyChanged(nameof(DetailText));
                break;
        }
    }
}
