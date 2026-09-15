using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

public sealed partial class DetailViewModel : ViewModelBase
{
    private const string FavoriteGlyphOutline = "\uEB51";
    private const string FavoriteGlyphFilled = "\uEB52";

    private readonly IWallpaperService _wallpaperService;
    private readonly ILocalLibrary _library;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IDownloadService _downloadService;
    private readonly IImageCacheService _imageCache;
    private readonly WallpaperProviderManager _providers;
    private readonly ILogger<DetailViewModel> _logger;
    private CancellationTokenSource? _downloadCts;
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

    public DetailViewModel(
        IWallpaperService wallpaperService,
        ILocalLibrary library,
        ISettingsService settingsService,
        IDialogService dialogs,
        IDownloadService downloadService,
        IImageCacheService imageCache,
        WallpaperProviderManager providers,
        ILocalizationService localization,
        ILogger<DetailViewModel> logger)
        : base(localization)
    {
        _wallpaperService = wallpaperService;
        _library = library;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _downloadService = downloadService;
        _imageCache = imageCache;
        _providers = providers;
        _logger = logger;

        DownloadProgressText = string.Empty;
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public bool HasWallpaper => Wallpaper is not null;

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
        && !IsDownloading;

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

        if (wallpaper is { Source: WallpaperSource.Online, HasLocalFile: false })
        {
            // Show the cached thumbnail right away while the full image is not on disk.
            _ = _imageCache.WarmThumbnailsAsync([wallpaper], PageToken);
        }
    }

    partial void OnWallpaperChanged(Wallpaper? value) => NotifyDerivedChanged();

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
    private async Task DownloadAsync()
    {
        if (Wallpaper is not { } wallpaper || string.IsNullOrEmpty(wallpaper.RemoteUrl))
        {
            SetError("Detail_Error_NoDownloadLink");
            return;
        }

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        IsDownloading = true;
        DownloadProgress = 0;
        SetSuccess(null);
        SetError(null);
        NotifyDerivedChanged();

        try
        {
            var folder = ResolveDownloadFolder();
            var progress = new Progress<double>(value =>
            {
                DownloadProgress = value;
                DownloadProgressText = $"{value * 100:0}%";
            });

            var sourceUrl = await _providers.GetDownloadUrlAsync(wallpaper, token).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                SetError("Detail_Error_NoDownloadLink");
                return;
            }

            var path = await _downloadService.DownloadAsync(sourceUrl, folder, wallpaper.Title, progress, token);

            if (ImageMetadataReader.TryReadDimensions(path, out var width, out var height))
            {
                wallpaper.Width = width;
                wallpaper.Height = height;
            }

            wallpaper.FileSize = new FileInfo(path).Length;
            wallpaper.LocalPath = path;
            await _library.SaveAsync(wallpaper);

            SetSuccess("Detail_Success_Downloaded", folder);
        }
        catch (OperationCanceledException)
        {
            SetSuccess("Detail_Success_DownloadCancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {Url}", wallpaper.RemoteUrl);
            SetError("Detail_Error_DownloadFailed");
        }
        finally
        {
            IsDownloading = false;
            DownloadProgressText = string.Empty;
            NotifyDerivedChanged();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

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
        OnPropertyChanged(nameof(CanShowInExplorer));
        OnPropertyChanged(nameof(HasTechnicalDetails));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(FavoriteGlyphText));
        OnPropertyChanged(nameof(SourceText));
    }
}
