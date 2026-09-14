using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
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
    private readonly ILogger<DetailViewModel> _logger;

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

    public DetailViewModel(
        IWallpaperService wallpaperService,
        ILocalLibrary library,
        ISettingsService settingsService,
        IDialogService dialogs,
        ILogger<DetailViewModel> logger)
    {
        _wallpaperService = wallpaperService;
        _library = library;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public bool HasWallpaper => Wallpaper is not null;

    public bool CanApplyWallpaper => !IsApplying && Wallpaper?.HasLocalFile == true;

    public bool HasMultipleMonitors => Monitors.Count > 1;

    public bool CanRemoveFromLibrary => Wallpaper is not null && _library.Find(Wallpaper.Id) is not null;

    public string ApplyButtonText => IsApplying ? "Applying…" : "Set as desktop wallpaper";

    public string FavoriteLabel => Wallpaper?.IsFavorite == true ? "Remove from favorites" : "Add to favorites";

    public string FavoriteGlyphText => Wallpaper?.IsFavorite == true ? FavoriteGlyphFilled : FavoriteGlyphOutline;

    public string SourceText => Wallpaper?.Source == WallpaperSource.Online ? "Online" : "On this PC";

    public void Load(Wallpaper? wallpaper)
    {
        Wallpaper = wallpaper;
        SelectedMonitor = null;
        SuccessNotice = null;
        ErrorNotice = null;
    }

    partial void OnWallpaperChanged(Wallpaper? value) => NotifyDerivedChanged();

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyWallpaper));
        OnPropertyChanged(nameof(ApplyButtonText));
    }

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
            await _wallpaperService.SetWallpaperAsync(path, fitMode, SelectedMonitor?.Id);

            _library.MarkUsed(Wallpaper.Id, DateTimeOffset.Now);
            var target = SelectedMonitor is null ? "all displays" : SelectedMonitor.DisplayName;
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

        _library.Remove(Wallpaper.Id);
        SuccessNotice = "Removed from your library.";
        ErrorNotice = null;
        NotifyDerivedChanged();
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Wallpaper is null)
        {
            return;
        }

        Wallpaper.IsFavorite = !Wallpaper.IsFavorite;
        NotifyDerivedChanged();
        SuccessNotice = Wallpaper.IsFavorite ? "Added to favorites." : "Removed from favorites.";
        ErrorNotice = null;
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
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(FavoriteGlyphText));
        OnPropertyChanged(nameof(SourceText));
    }
}
