using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>Maps wallpaper fit modes to their localization resource keys.</summary>
internal static class FitModeKeys
{
    public static string For(WallpaperFitMode mode) => mode switch
    {
        WallpaperFitMode.Fill => "Fit_Fill",
        WallpaperFitMode.Fit => "Fit_Fit",
        WallpaperFitMode.Stretch => "Fit_Stretch",
        WallpaperFitMode.Center => "Fit_Center",
        WallpaperFitMode.Tile => "Fit_Tile",
        WallpaperFitMode.Span => "Fit_Span",
        _ => "Fit_Fill",
    };
}
