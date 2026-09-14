namespace Muralis.Core.Models;

/// <summary>
/// How the wallpaper image is fitted to the desktop. Values map to the
/// <c>WallpaperStyle</c>/<c>TileWallpaper</c> registry values on Windows.
/// </summary>
public enum WallpaperFitMode
{
    /// <summary>Crop to fill the screen (WallpaperStyle 10).</summary>
    Fill,

    /// <summary>Letterbox the whole image inside the screen (WallpaperStyle 6).</summary>
    Fit,

    /// <summary>Distort to fill the screen (WallpaperStyle 2).</summary>
    Stretch,

    /// <summary>Center at native size (WallpaperStyle 0).</summary>
    Center,

    /// <summary>Repeat the image in a mosaic (TileWallpaper 1).</summary>
    Tile,

    /// <summary>Stretch one image across all displays (WallpaperStyle 22).</summary>
    Span,
}
