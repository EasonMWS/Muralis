using Microsoft.Extensions.Logging;

namespace Muralis.Core.Desktop;

/// <summary>
/// What one look at the user's desktop means for the canvas: which entries would be added, which are
/// there already, which the user has turned down, and which cannot be shown at all. Counted rather than
/// applied, so a first run can say what it is about to do before it does anything.
/// </summary>
/// <remarks>
/// The comparison is by source path, never by caption: the shell's caption for an entry is a display
/// detail that may be a file name, a shortcut's own title or an address's host, and it is not a
/// durable identity. The path a file is at is.
/// </remarks>
public sealed record DesktopAdoptionPlan(
    IReadOnlyList<DesktopContentEntry> ToAdopt,
    IReadOnlyList<DesktopContentEntry> AlreadyAdopted,
    IReadOnlyList<DesktopContentEntry> Declined,
    IReadOnlyList<DesktopContentSkip> Unsupported,
    IReadOnlyList<string> UnreadableFolders)
{
    /// <summary>Nothing to say, for a caller that has not read the desktop yet.</summary>
    public static DesktopAdoptionPlan None { get; } = new([], [], [], [], []);

    /// <summary>Whether there is anything at all to report.</summary>
    public bool IsEmpty =>
        ToAdopt.Count == 0
        && AlreadyAdopted.Count == 0
        && Declined.Count == 0
        && Unsupported.Count == 0
        && UnreadableFolders.Count == 0;
}

/// <summary>The items one adoption added, in the order they were placed.</summary>
public sealed record DesktopAdoptionResult(IReadOnlyList<DesktopItem> Added)
{
    /// <summary>A run that added nothing.</summary>
    public static DesktopAdoptionResult None { get; } = new([]);
}

/// <summary>
/// Reads what a scan means and writes what it decided. Two steps, deliberately: the plan changes
/// nothing and can be shown to the user first, and only <see cref="Adopt"/> edits the layout.
/// </summary>
/// <remarks>
/// Adopting is one-way and shallow: an item is created that points at where the file already is, and
/// the file itself is never copied, moved, renamed or rewritten. An entry the user took off the canvas
/// before stays off — that is what the layout's turned-down list is for — so a sync cannot undo a
/// removal the user made on purpose.
/// </remarks>
public static class DesktopContentAdopter
{
    /// <summary>
    /// Compares a scan with a layout. An entry is already adopted when an item names its source path;
    /// it is declined when the user has turned that source down; everything else is to be adopted.
    /// </summary>
    public static DesktopAdoptionPlan Plan(DesktopContentScan scan, DesktopLayout layout)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(layout);

        var sources = layout.Items
            .Where(item => item is not null && item.SourcePath.Length > 0)
            .Select(item => item.SourcePath)
            .ToList();

        var toAdopt = new List<DesktopContentEntry>();
        var alreadyAdopted = new List<DesktopContentEntry>();
        var declined = new List<DesktopContentEntry>();

        foreach (var entry in scan.Adoptable)
        {
            if (sources.Any(source => entry.IsSameSourceAs(source)))
            {
                alreadyAdopted.Add(entry);
            }
            else if (layout.Takeover.IsIgnored(entry.SourcePath))
            {
                declined.Add(entry);
            }
            else
            {
                toAdopt.Add(entry);
            }
        }

        return new DesktopAdoptionPlan(
            toAdopt,
            alreadyAdopted,
            declined,
            scan.Skipped,
            scan.UnreadableFolders);
    }

    /// <summary>
    /// Adds the planned entries to the layout, each with a free place on the display, and returns the
    /// items it made. Nothing is adopted while the layout says adoption is off, and nothing is adopted
    /// twice: the plan is a snapshot, and a layout that changed since it was made is re-checked here.
    /// </summary>
    public static DesktopAdoptionResult Adopt(
        DesktopAdoptionPlan plan,
        DesktopLayout layout,
        double displayWidthDip,
        double displayHeightDip,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(layout);

        if (!layout.Takeover.AdoptDesktopItems || plan.ToAdopt.Count == 0)
        {
            return DesktopAdoptionResult.None;
        }

        var added = new List<DesktopItem>(plan.ToAdopt.Count);
        foreach (var entry in plan.ToAdopt)
        {
            // The plan may be a moment old; an item added since it was made wins, so the same file
            // cannot end up on the canvas twice.
            if (layout.Items.Any(item => entry.IsSameSourceAs(item?.SourcePath)))
            {
                continue;
            }

            var item = DesktopItemFactory.CreateFromTarget(entry.Target, entry.Name, entry.SourcePath);
            var (x, y) = DesktopItemPlacer.NextFreeSpot(
                layout.Items,
                layout.Dock.DockedItemIds(),
                displayWidthDip,
                displayHeightDip,
                item.SizeDip);

            item.OffsetXDip = x;
            item.OffsetYDip = y;
            layout.Items.Add(item);
            added.Add(item);
        }

        if (added.Count > 0)
        {
            logger?.LogInformation(
                "The desktop was adopted: {Added} items were added, {Declined} entries the user turned down were left off, {Unsupported} entries could not be shown",
                added.Count,
                plan.Declined.Count,
                plan.Unsupported.Count);
        }

        return new DesktopAdoptionResult(added);
    }
}
