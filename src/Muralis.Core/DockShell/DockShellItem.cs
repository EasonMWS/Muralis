namespace Muralis.Core.DockShell;

/// <summary>What an item represents. Desktop Shelf items use the four shell-facing kinds.</summary>
public enum DockShellItemType
{
    Application,
    File,
    Folder,
    Shortcut,
    SpecialShellItem,
    Utility,
}

[Flags]
public enum DockShellItemCapabilities
{
    None = 0,
    Open = 1,
    ContextMenu = 2,
    Drag = 4,
}

/// <summary>
/// Lightweight identity shared by pinned apps, the Desktop Shelf, and utilities. <see cref="Identity"/>
/// is either a path or a stable Shell identity; the Dock never claims ownership of that target.
/// </summary>
public sealed record DockShellItem(
    string Id,
    string DisplayName,
    string Glyph,
    string Identity,
    DockShellItemType ItemType,
    DockShellItemCapabilities Capabilities = DockShellItemCapabilities.Open)
{
    public bool CanOpen => Capabilities.HasFlag(DockShellItemCapabilities.Open);

    public bool CanShowContextMenu => Capabilities.HasFlag(DockShellItemCapabilities.ContextMenu);

    public bool CanDrag => Capabilities.HasFlag(DockShellItemCapabilities.Drag);
}

/// <summary>Future behavior seam. The Phase 4A shell supplies mock identities and performs no actions.</summary>
public interface IDockShellItemActionHandler
{
    Task OpenAsync(DockShellItem item, CancellationToken cancellationToken = default);

    Task ShowContextMenuAsync(DockShellItem item, CancellationToken cancellationToken = default);

    Task BeginDragAsync(DockShellItem item, CancellationToken cancellationToken = default);
}
