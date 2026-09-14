using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class DetailViewModel : ObservableObject
{
    private const string FavoriteGlyph = "\uEB51";
    private const string FavoriteGlyphFilled = "\uEB52";

    private readonly ILogger<DetailViewModel> _logger;

    [ObservableProperty]
    public partial Wallpaper? Wallpaper { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    public DetailViewModel(ILogger<DetailViewModel> logger) => _logger = logger;

    public bool HasWallpaper => Wallpaper is not null;

    public string FavoriteLabel => Wallpaper?.IsFavorite == true ? "Remove from favorites" : "Add to favorites";

    public string FavoriteGlyphText => Wallpaper?.IsFavorite == true ? FavoriteGlyphFilled : FavoriteGlyph;

    public string SourceText => Wallpaper?.Source == WallpaperSource.Online ? "Online" : "On this PC";

    public void Load(Wallpaper? wallpaper)
    {
        Wallpaper = wallpaper;
        StatusMessage = null;
        StatusIsError = false;
    }

    partial void OnWallpaperChanged(Wallpaper? value) => NotifyDerivedChanged();

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Wallpaper is null)
        {
            return;
        }

        Wallpaper.IsFavorite = !Wallpaper.IsFavorite;
        NotifyDerivedChanged();
        SetStatus(Wallpaper.IsFavorite ? "Added to favorites." : "Removed from favorites.", isError: false);
    }

    [RelayCommand]
    private void SetAsWallpaper() =>
        SetStatus("Applying wallpapers to the desktop arrives in the next milestone (M2).", isError: false);

    [RelayCommand]
    private void Download() =>
        SetStatus("Downloads arrive together with the online provider (M4).", isError: false);

    [RelayCommand]
    private void OpenInExplorer()
    {
        var path = Wallpaper?.LocalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SetStatus("This file is not available on disk yet.", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open Explorer for {Path}", path);
            SetStatus("Could not open the file location.", isError: true);
        }
    }

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasWallpaper));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(FavoriteGlyphText));
        OnPropertyChanged(nameof(SourceText));
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
