namespace Muralis.Core.Models;

/// <summary>
/// Rules for wallpaper tags. Tags are user-editable but also carry provider metadata
/// (source, category), so they are normalized instead of validated: casing is preserved,
/// duplicates are matched case-insensitively, and separator characters never survive
/// because the catalog stores the list as a single comma-separated column.
/// </summary>
public static class WallpaperTags
{
    public const int MaxTagLength = 32;

    public const int MaxTagsPerWallpaper = 20;

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ';'];

    /// <summary>Cleans a raw tag: separators collapsed, length capped. Returns <c>null</c> when nothing usable remains.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var parts = tag.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var cleaned = string.Join(' ', parts);
        return cleaned.Length > MaxTagLength
            ? cleaned[..MaxTagLength].TrimEnd()
            : cleaned;
    }

    public static bool Contains(IEnumerable<string> tags, string? tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes a tag regardless of casing. Returns whether the list changed.</summary>
    public static bool Remove(IList<string> tags, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        for (var index = 0; index < tags.Count; index++)
        {
            if (string.Equals(tags[index], tag, StringComparison.OrdinalIgnoreCase))
            {
                tags.RemoveAt(index);
                return true;
            }
        }

        return false;
    }
}
