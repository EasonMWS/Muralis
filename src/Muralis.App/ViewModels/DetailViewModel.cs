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

public sealed partial class DetailViewModel : ObservableObject
{
    private const string FavoriteGlyphOutline = "\uEB51";
    private const string FavoriteGlyphFilled = "\uEB52";

    private readonly IWallpaperService _wallpaperService;
    private readonly ILocalLibrary _library;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IDownloadService _downloadService;
    private readonly IImageCacheService _imageCache;
    private readonly ILogger<DetailViewModel> _logger;
    private CancellationTokenSource? _downloadCts;

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
        ILogger<DetailViewModel> logger)
    {
        _wallpaperService = wallpaperService;
        _library = library;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _downloadService = downloadService;
        _imageCache = imageCache;
        _logger = logger;

        DownloadProgressText = string.Empty;
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public bool HasWallpaper => Wallpaper is not null;

    public bool CanApplyWallpaper => !IsApplying && Wallpaper?.HasLocalFile == true;

    public bool HasMultipleMonitors => Monitors.Count > 1;

    public bool CanRemoveFromLibrary =>
        Wallpaper is { Source: WallpaperSource.Local } && _library.Find(Wallpaper.Id) is not null;

    public string ApplyButtonText => IsApplying ? "Applying…" : "Set as desktop wallpaper";

    public string FavoriteLabel => Wallpaper?.IsFavorite == true ? "Remove from favorites" : "Add to favorites";

    public string FavoriteGlyphText => Wallpaper?.IsFavorite == true ? FavoriteGlyphFilled : FavoriteGlyphOutline;

    public string SourceText => Wallpaper?.Source == WallpaperSource.Online ? "Online" : "On this PC";

    public bool CanDownload =>
        Wallpaper is { Source: WallpaperSource.Online, HasLocalFile: false }
        && !string.IsNullOrEmpty(Wallpaper.RemoteUrl)
        && !IsDownloading;

    public bool CanShowInExplorer => Wallpaper?.HasLocalFile == true;

    public void Load(Wallpaper? wallpaper)
    {
        Wallpaper = wallpaper;
        SelectedMonitor = null;
        SuccessNotice = null;
        ErrorNotice = null;
        DownloadProgress = 0;
        DownloadProgressText = string.Empty;

        if (wallpaper is { Source: WallpaperSource.Online, HasLocalFile: false })
        {
            // Show the cached thumbnail right away while the full image is not on disk.
            _ = _imageCache.WarmThumbnailsAsync([wallpaper]);
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
            ErrorNotice = "This wallpaper has no local file to apply yet.";
            return;
        }

        IsApplying = true;
        SuccessNotice = null;
        ErrorNotice = null;

        try
        {
            var fitMode = _settingsService.Current.DefaultFitMode;
            var target = SelectedMonitor is null ? "all displays" : SelectedMonitor.DisplayName;

            await _wallpaperService.SetWallpaperAsync(path, fitMode, SelectedMonitor?.Id);
            await _library.RecordUsageAsync(Wallpaper, target);

            SuccessNotice = $"Desktop wallpaper updated — {fitMode} on {target}.";
        }
        catch (NotSupportedException ex)
        {
            ErrorNotice = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set the desktop wallpaper");
            ErrorNotice = "Could not update the desktop wallpaper. See the log for details.";
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
            ErrorNotice = "This wallpaper has no download link.";
            return;
        }

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        IsDownloading = true;
        DownloadProgress = 0;
        SuccessNotice = null;
        ErrorNotice = null;
        NotifyDerivedChanged();

        try
        {
            var folder = ResolveDownloadFolder();
            var progress = new Progress<double>(value =>
            {
                DownloadProgress = value;
                DownloadProgressText = $"{value * 100:0}%";
            });

            var path = await _downloadService.DownloadAsync(wallpaper.RemoteUrl, folder, wallpaper.Title, progress, token);

            if (ImageMetadataReader.TryReadDimensions(path, out var width, out var height))
            {
                wallpaper.Width = width;
                wallpaper.Height = height;
            }

            wallpaper.FileSize = new FileInfo(path).Length;
            wallpaper.LocalPath = path;
            await _library.SaveAsync(wallpaper);

            SuccessNotice = $"Downloaded to {folder}.";
        }
        catch (OperationCanceledException)
        {
            SuccessNotice = "Download cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {Url}", wallpaper.RemoteUrl);
            ErrorNotice = "The download failed. Check your connection and try again.";
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
            "Remove from library",
            $"Remove \"{Wallpaper.Title}\" from Muralis? The original file stays on your disk.",
            "Remove");

        if (!confirmed)
        {
            return;
        }

        await _library.RemoveAsync(Wallpaper.Id);
        SuccessNotice = "Removed from your library.";
        ErrorNotice = null;
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
            SuccessNotice = Wallpaper.IsFavorite ? "Added to favorites." : "Removed from favorites.";
            ErrorNotice = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the favorite state");
            ErrorNotice = "Could not update favorites. See the log for details.";
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
            ErrorNotice = "This file is not available on disk yet.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open Explorer for {Path}", path);
            ErrorNotice = "Could not open the file location.";
        }
    }

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasWallpaper));
        OnPropertyChanged(nameof(CanApplyWallpaper));
        OnPropertyChanged(nameof(CanRemoveFromLibrary));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanShowInExplorer));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(FavoriteGlyphText));
        OnPropertyChanged(nameof(SourceText));
    }
}
