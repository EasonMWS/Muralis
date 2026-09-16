namespace Muralis.Core.Desktop;

/// <summary>
/// One entry of the user's own desktop that can become a canvas item. It holds a target that already
/// points at where the file is: adopting an entry never copies, moves or rewrites anything, and the
/// source path is kept so a later sync recognises the same file instead of adding a second item for it.
/// </summary>
public sealed record DesktopContentEntry(string SourcePath, string Name, DesktopItemTarget Target)
{
    /// <summary>Whether two entries are two sightings of the same desktop file.</summary>
    public bool IsSameSourceAs(string? sourcePath) =>
        !string.IsNullOrEmpty(sourcePath)
        && string.Equals(SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A desktop entry that cannot become an item, kept with the reason so the first-run summary can say
/// what it left out rather than looking like it missed something. Nothing about a skipped entry is
/// changed: it stays on the desktop as it was.
/// </summary>
public sealed record DesktopContentSkip(string SourcePath, string Name, string Reason);

/// <summary>
/// What one look at the user's desktop found. The two lists are the whole of it: what would be adopted
/// and what would not, with a line for each folder that could not be read at all — a desktop on a
/// disconnected drive is a problem to report, not an empty desktop to believe in.
/// </summary>
/// <remarks>
/// A scan is a snapshot and nothing more: it holds no handles, starts no watching and keeps no state
/// between calls, so scanning costs one directory listing per desktop folder and nothing while idle.
/// </remarks>
public sealed record DesktopContentScan(
    IReadOnlyList<DesktopContentEntry> Adoptable,
    IReadOnlyList<DesktopContentSkip> Skipped,
    IReadOnlyList<string> UnreadableFolders)
{
    /// <summary>What a scan of nowhere looks like, for a caller that has not scanned yet.</summary>
    public static DesktopContentScan Empty { get; } = new([], [], []);

    /// <summary>Whether adopting everything here would put anything on the canvas.</summary>
    public bool HasEntries => Adoptable.Count > 0;
}
