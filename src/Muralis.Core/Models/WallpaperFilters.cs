namespace Muralis.Core.Models;

/// <summary>Which image shapes a wallpaper search may return.</summary>
public enum WallpaperOrientation
{
    Any,
    Landscape,
    Portrait,
}

/// <summary>Smallest long-edge class a wallpaper search may return ("2K" is 2560, "4K" is 3840).</summary>
public enum WallpaperResolution
{
    Any,
    FullHd,
    QuadHd,
    UltraHd,
}

/// <summary>
/// Filters that providers may apply server-side. Providers without native filter support
/// are handled client-side by <see cref="Matches"/> — items whose dimensions are unknown
/// (some APIs only report them on download) are kept rather than dropped by guesswork.
/// </summary>
public static class WallpaperFilter
{
    public static bool IsActive(WallpaperOrientation orientation, WallpaperResolution resolution) =>
        orientation != WallpaperOrientation.Any || resolution != WallpaperResolution.Any;

    public static bool Matches(
        Wallpaper wallpaper,
        WallpaperOrientation orientation,
        WallpaperResolution resolution)
    {
        if (wallpaper.Width <= 0 || wallpaper.Height <= 0)
        {
            return true;
        }

        if (orientation != WallpaperOrientation.Any)
        {
            var isLandscape = wallpaper.Width >= wallpaper.Height;
            var wanted = orientation == WallpaperOrientation.Landscape;
            if (isLandscape != wanted)
            {
                return false;
            }
        }

        return resolution == WallpaperResolution.Any
            || Math.Max(wallpaper.Width, wallpaper.Height) >= LongEdge(resolution);
    }

    /// <summary>The long edge, in pixels, that a resolution class requires.</summary>
    public static int LongEdge(WallpaperResolution resolution) => resolution switch
    {
        WallpaperResolution.FullHd => 1920,
        WallpaperResolution.QuadHd => 2560,
        WallpaperResolution.UltraHd => 3840,
        _ => 0,
    };

    /// <summary>Minimum width and height to send to APIs that take a minimum size.</summary>
    public static (int Width, int Height) MinimumSize(WallpaperResolution resolution) => resolution switch
    {
        WallpaperResolution.FullHd => (1920, 1080),
        WallpaperResolution.QuadHd => (2560, 1440),
        WallpaperResolution.UltraHd => (3840, 2160),
        _ => (0, 0),
    };
}
