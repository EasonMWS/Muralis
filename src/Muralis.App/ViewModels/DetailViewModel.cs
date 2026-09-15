using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class DetailViewModel : ViewModelBase
{
    private const string FavoriteGlyphOutline = "\uEB51";
    private const string FavoriteGlyphFilled = "\uEB52";

    private readonly IWallpaperService _wallpaperService;
    private readonly ILocalLibrary _library;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IDownloadQueue _downloadQueue;
    private readonly IImageCacheService _imageCache;
    private readonly ILogger<DetailViewModel> _logger;
    private DownloadItem? _downloadItem;
    private Notice? _successNotice;
    private Notice? _errorNotice;

    [ObservableProperty]
    public partial Wallpaper? Wallpaper { get; set; }

    [ObservableProperty]
    public partial string? SuccessNotice { get; set; }

    [ObservableProperty]
    public partial string? ErrorNotice { get; set; }

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    [ObservableProperty]
    public partial MonitorInfo? SelectedMonitor { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    public partial string DownloadProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewTag { get; set; } = string.Empty;

    public DetailViewModel(
        IWallpaperService wallpaperService,
        ILocalLibrary library,
        ISettingsService settingsService,
        IDialogService dialogs,
        IDownloadQueue downloadQueue,
        IImageCacheService imageCache,
        ILocalizationService localization,
        ILogger<DetailViewModel> logger)
        : base(localization)
    {
        _wallpaperService = wallpaperService;
        _library = library;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _downloadQueue = downloadQueue;
        _imageCache = imageCache;
        _logger = logger;

        DownloadProgressText = string.Empty;
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public ObservableCollection<TagChip> Tags { get; } = [];

    public bool HasWallpaper => Wallpaper is not null;

    public bool HasTags => Tags.Count > 0;

    public bool CanAddTag => Wallpaper is not null && Tags.Count < WallpaperTags.MaxTagsPerWallpaper;

    public int MaxTagLength => WallpaperTags.MaxTagLength;

    public bool CanApplyWallpaper => !IsApplying && Wallpaper?.HasLocalFile == true;

    public bool HasMultipleMonitors => Monitors.Count > 1;

    public bool CanRemoveFromLibrary =>
        Wallpaper is { Source: WallpaperSource.Local } && _library.Find(Wallpaper.Id) is not null;

    public string ApplyButtonText => Loc.Get(IsApplying ? "Detail_Applying" : "Detail_SetWallpaper");

    public string FavoriteLabel => Loc.Get(Wallpaper?.IsFavorite == true ? "Detail_RemoveFavorite" : "Detail_AddFavorite");

    public string FavoriteGlyphText => Wallpaper?.IsFavorite == true ? FavoriteGlyphFilled : FavoriteGlyphOutline;

    public string SourceText => Loc.Get(Wallpaper?.Source == WallpaperSource.Online ? "Detail_Source_Online" : "Detail_Source_Local");

    public bool CanDownload =>
        Wallpaper is { Source: WallpaperSource.Online, HasLocalFile: false }
        && !string.IsNullOrEmpty(Wallpaper.RemoteUrl)
        && !IsDownloading
        && !CanRetryDownload;

    /// <summary>Offered after the queue gave up (or the user cancelled); retries the same item.</summary>
    public bool CanRetryDownload => _downloadItem?.State is DownloadState.Failed or DownloadState.Cancelled;

    public bool CanShowInExplorer => Wallpaper?.HasLocalFile == true;

    /// <summary>True when the info card has at least one fact to show; otherwise the card is hidden.</summary>
    public bool HasTechnicalDetails =>
        Wallpaper is { } wallpaper
        && (!string.IsNullOrEmpty(wallpaper.AspectRatioText)
            || !string.IsNullOrEmpty(wallpaper.FileSizeText)
            || wallpaper.HasLocalFile);

    public override void OnLanguageChanged()
    {
        if (_successNotice is { } success)
        {
            SuccessNotice = Loc.Format(success.Key, success.Args);
        }

        if (_errorNotice is { } error)
        {
            ErrorNotice = Loc.Format(error.Key, error.Args);
        }

        RebuildTags();
        NotifyDerivedChanged();
    }

    /// <summary>A pending notice, kept as a resource key so it survives language changes.</summary>
    private sealed record Notice(string Key, object?[] Args);

    public void Load(Wallpaper? wallpaper)
    {
        Wallpaper = wallpaper;
        SelectedMonitor = null;
        SetSuccess(null);
        SetError(null);
        DownloadProgress = 0;
        DownloadProgressText = string.Empty;

        // A download started earlier (here or from the queue page) is still running;
        // follow it rather than pretending nothing is happening.
        AttachTo(wallpaper is null ? null : _downloadQueue.Find(wallpaper.Id));

        if (wallpaper is { Source: WallpaperSource.Online, HasLocalFile: false })
        {
            // Show the cached thumbnail right away while the full image is not on disk.
            _ = _imageCache.WarmThumbnailsAsync([wallpaper], PageToken);
        }
    }

    partial void OnWallpaperChanged(Wallpaper? value)
    {
        NewTag = string.Empty;
        RebuildTags();
        NotifyDerivedChanged();
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyWallpaper));
        OnPropertyChanged(nameof(ApplyButtonText));
    }

    partial void OnIsDownloadingChanged(bool value) => NotifyDerivedChanged();

    [RelayCommand]
    private async Task LoadMonitorsAsync()
    {
        try
        {
            var monitors = await _wallpaperService.GetMonitorsAsync();
            Monitors.Clear();
            foreach (var monitor in monitors)
            {
                Monitors.Add(monitor);
            }

            OnPropertyChanged(nameof(HasMultipleMonitors));
        }
        catch (Exception ex)
        {
            // Monitor enumeration is a progressive enhancement; the wallpaper can still
            // be applied to all displays without it.
            _logger.LogWarning(ex, "Could not enumerate displays");
        }
    }

    [RelayCommand]
    private async Task SetAsWallpaperAsync()
    {
        if (Wallpaper?.LocalPath is not { Length: > 0 } path)
        {
            SetError("Detail_Error_NoLocalFile");
            return;
        }

        IsApplying = true;
        SetSuccess(null);
        SetError(null);

        try
        {
            var fitMode = _settingsService.Current.DefaultFitMode;
            var target = SelectedMonitor is null
                ? Loc.Get("Detail_Target_AllDisplays")
                : SelectedMonitor.DisplayName;

            await _wallpaperService.SetWallpaperAsync(path, fitMode, SelectedMonitor?.Id);
            await _library.RecordUsageAsync(Wallpaper, target);

            SetSuccess("Detail_Success_Applied", Loc.Get(FitModeKeys.For(fitMode)), target);
        }
        catch (NotSupportedException ex)
        {
            // Platform capability message; keep the original text from the service.
            _errorNotice = null;
            ErrorNotice = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set the desktop wallpaper");
            SetError("Detail_Error_ApplyFailed");
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void Download()
    {
        if (Wallpaper is not { } wallpaper || string.IsNullOrEmpty(wallpaper.RemoteUrl))
        {
            SetError("Detail_Error_NoDownloadLink");
            return;
        }

        SetSuccess(null);
        SetError(null);

        // The queue owns the transfer, so it keeps running when the user leaves this page.
        var item = _downloadQueue.Enqueue(wallpaper, ResolveDownloadFolder());
        if (ReferenceEquals(item, _downloadItem) || item.State == DownloadState.Completed)
        {
            return;
        }

        AttachTo(item);
    }

    [RelayCommand]
    private void CancelDownload()
    {
        if (_downloadItem is { } item)
        {
            _downloadQueue.Cancel(item);
        }
    }

    [RelayCommand]
    private void RetryDownload()
    {
        if (_downloadItem is { } item)
        {
            SetError(null);
            _downloadQueue.Retry(item);
        }
    }

    /// <summary>
    /// Follows one queue item while this page is open. A page opened while a download is
    /// already running attaches to it instead of starting a second transfer.
    /// </summary>
    private void AttachTo(DownloadItem? item)
    {
        if (ReferenceEquals(item, _downloadItem))
        {
            return;
        }

        if (_downloadItem is { } previous)
        {
            previous.PropertyChanged -= OnDownloadItemChanged;
        }

        _downloadItem = item;
        if (item is not null)
        {
            item.PropertyChanged += OnDownloadItemChanged;
        }

        ApplyDownloadState(item, announce: false);
    }

    private void OnDownloadItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DownloadItem.Progress):
                DownloadProgress = _downloadItem?.Progress ?? 0;
                DownloadProgressText = $"{DownloadProgress * 100:0}%";
                break;
            case nameof(DownloadItem.State):
                ApplyDownloadState(_downloadItem);
                break;
        }
    }

    private void ApplyDownloadState(DownloadItem? item, bool announce = true)
    {
        IsDownloading = item?.State == DownloadState.Downloading;
        DownloadProgress = item?.Progress ?? 0;
        DownloadProgressText = item?.State == DownloadState.Downloading ? $"{DownloadProgress * 100:0}%" : string.Empty;

        if (announce)
        {
            switch (item?.State)
            {
                case DownloadState.Completed:
                    SetSuccess("Detail_Success_Downloaded", ResolveDownloadFolder());
                    SetError(null);
                    break;
                case DownloadState.Failed:
                    SetError("Detail_Error_DownloadFailed");
                    SetSuccess(null);
                    break;
                case DownloadState.Cancelled:
                    SetSuccess("Detail_Success_DownloadCancelled");
                    SetError(null);
                    break;
            }
        }

        NotifyDerivedChanged();
    }

    public override void DetachFromPage()
    {
        AttachTo(null);
        base.DetachFromPage();
    }

    /// <summary>Adds the text in the tag box to the wallpaper, skipping duplicates and empty input.</summary>
    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (Wallpaper is not { } wallpaper)
        {
            return;
        }

        var tag = WallpaperTags.Normalize(NewTag);
        NewTag = string.Empty;
        if (tag is null || WallpaperTags.Contains(wallpaper.Tags, tag))
        {
            return;
        }

        if (Tags.Count >= WallpaperTags.MaxTagsPerWallpaper)
        {
            SetError("Detail_Error_TagLimit");
            return;
        }

        var updated = wallpaper.Tags.ToList();
        updated.Add(tag);
        wallpaper.Tags = updated;
        await SaveTagsAsync(wallpaper);
    }

    private async Task RemoveTagAsync(string tag)
    {
        if (Wallpaper is not { } wallpaper)
        {
            return;
        }

        var updated = wallpaper.Tags.ToList();
        if (!WallpaperTags.Remove(updated, tag))
        {
            return;
        }

        wallpaper.Tags = updated;
        await SaveTagsAsync(wallpaper);
    }

    /// <summary>
    /// Persists tag edits through the catalog. Like favoriting, this adds a record for an
    /// online wallpaper so the tags survive restarts.
    /// </summary>
    private async Task SaveTagsAsync(Wallpaper wallpaper)
    {
        try
        {
            await _library.SaveAsync(wallpaper);
            SetError(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save tags for {Id}", wallpaper.Id);
            SetError("Detail_Error_TagsFailed");
        }

        RebuildTags();
    }

    private void RebuildTags()
    {
        Tags.Clear();
        if (Wallpaper is { } wallpaper)
        {
            var removeLabel = Loc.Get("Detail_RemoveTag");
            foreach (var tag in wallpaper.Tags)
            {
                Tags.Add(new TagChip(tag, removeLabel, value => _ = RemoveTagAsync(value)));
            }
        }

        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(CanAddTag));
    }

    private string ResolveDownloadFolder()
    {
        var configured = _settingsService.Current.DownloadFolder;
        return string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultDownloadFolder : configured;
    }

    [RelayCommand]
    private async Task RemoveFromLibraryAsync()
    {
        if (Wallpaper is null || !CanRemoveFromLibrary)
        {
            return;
        }

        var confirmed = await _dialogs.ShowConfirmAsync(
            Loc.Get("Detail_ConfirmRemove_Title"),
            Loc.Format("Detail_ConfirmRemove_Message", Wallpaper.Title),
            Loc.Get("Detail_ConfirmRemove_Primary"),
            Loc.Get("Common_Cancel"));

        if (!confirmed)
        {
            return;
        }

        await _library.RemoveAsync(Wallpaper.Id);
        SetSuccess("Detail_Success_RemovedFromLibrary");
        SetError(null);
        NotifyDerivedChanged();
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Wallpaper is null)
        {
            return;
        }

        try
        {
            await _library.SetFavoriteAsync(Wallpaper, !Wallpaper.IsFavorite);
            SetSuccess(Wallpaper.IsFavorite ? "Detail_Success_FavoriteAdded" : "Detail_Success_FavoriteRemoved");
            SetError(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the favorite state");
            SetError("Detail_Error_FavoriteFailed");
        }
        finally
        {
            NotifyDerivedChanged();
        }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        var path = Wallpaper?.LocalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SetError("Detail_Error_FileMissing");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open Explorer for {Path}", path);
            SetError("Detail_Error_OpenInExplorer");
        }
    }

    private void SetSuccess(string? key, params object?[] args)
    {
        _successNotice = key is null ? null : new Notice(key, args);
        SuccessNotice = key is null ? null : Loc.Format(key, args);
    }

    private void SetError(string? key, params object?[] args)
    {
        _errorNotice = key is null ? null : new Notice(key, args);
        ErrorNotice = key is null ? null : Loc.Format(key, args);
    }

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasWallpaper));
        OnPropertyChanged(nameof(CanApplyWallpaper));
        OnPropertyChanged(nameof(CanRemoveFromLibrary));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRetryDownload));
        OnPropertyChanged(nameof(CanShowInExplorer));
        OnPropertyChanged(nameof(HasTechnicalDetails));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(FavoriteGlyphText));
        OnPropertyChanged(nameof(SourceText));
    }
}
