namespace Muralis.Core.Dock;

/// <summary>
/// One place in the dock, held as a reference to an item rather than a copy of it: the item itself —
/// its name, its target, where it sits when it is not in the dock — stays in the layout's item list
/// and is only ever pointed at from here.
/// </summary>
/// <remarks>
/// The dock's order is the position of the entries in <see cref="DockOptions.Entries"/>, so there is
/// one answer to "what comes first" and nothing to keep in step. A per-entry attribute — pinning,
/// a group, a separator — extends this object; it does not change the shape of the document.
/// </remarks>
public sealed class DockEntry
{
    /// <summary>The id of a <c>DesktopItem</c> in the same layout document.</summary>
    public string ItemId { get; set; } = string.Empty;

    public DockEntry Clone() => new() { ItemId = ItemId };

    /// <summary>A short form for log lines and error messages.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(ItemId) ? "(no item)" : ItemId;
}
