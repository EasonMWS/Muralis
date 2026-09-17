namespace Muralis.Core.DockShell;

public enum DesktopShelfItemSource
{
    User,
    Public,
    Shell,
}

/// <summary>A read-only projection of an item Explorer exposes on the desktop.</summary>
public sealed record DesktopShelfItem(
    string Identity,
    string DisplayName,
    string Path,
    DockShellItemType ItemType,
    DesktopShelfItemSource Source)
{
    public string IconKey => Path;
}

public sealed record DesktopShelfSnapshot(
    IReadOnlyList<DesktopShelfItem> Items,
    int UserCount,
    int PublicCount,
    DateTimeOffset RefreshedAt,
    TimeSpan EnumerationDuration,
    IReadOnlyList<string> UnreadableFolders)
{
    public static DesktopShelfSnapshot Empty { get; } =
        new([], 0, 0, DateTimeOffset.MinValue, TimeSpan.Zero, []);
}
