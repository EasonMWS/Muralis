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
/// The kind of wallpaper a source classifies an item as (Wallhaven: general, anime, people).
/// On a query, <see cref="Any"/> means no narrowing; on a wallpaper, it means the source
/// does not classify the item.
/// </summary>
public enum WallpaperCategory
{
    Any,
    General,
    Anime,
    People,
}

/// <summary>
/// Filters that providers may apply server-side. Providers without native filter support
/// are handled client-side by <see cref="Matches"/> — items whose dimensions are unknown
/// (some APIs only report them on download) are kept rather than dropped by guesswork.
/// A category is different: it is a positive attribute, so an unclassified item never
/// matches a category filter.
/// </summary>
public static class WallpaperFilter
{
    public static bool IsActive(
        WallpaperOrientation orientation,
        WallpaperResolution resolution,
        WallpaperCategory category) =>
        orientation != WallpaperOrientation.Any
        || resolution != WallpaperResolution.Any
        || category != WallpaperCategory.Any;

    public static bool Matches(
        Wallpaper wallpaper,
        WallpaperOrientation orientation,
        WallpaperResolution resolution,
        WallpaperCategory category)
    {
        if (category != WallpaperCategory.Any && wallpaper.Category != category)
        {
            return false;
        }

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
