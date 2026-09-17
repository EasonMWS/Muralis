using Muralis.Core.Dock;

namespace Muralis.Core.DockShell;

/// <summary>How one attempt to pin an application ended.</summary>
public enum PinnedAppAddOutcome
{
    Added,

    /// <summary>The application is already pinned; <see cref="PinnedAppAddResult.ExistingId"/> names it.</summary>
    Duplicate,

    /// <summary>The dock is full.</summary>
    LimitReached,

    /// <summary>The path is not an application the dock pins, or could not be read at all.</summary>
    Unsupported,
}

/// <summary>The result of one add attempt. A refusal is an outcome rather than an exception, because
/// every one of them is something to tell the user about in the dock's own words.</summary>
public sealed record PinnedAppAddResult(
    PinnedAppAddOutcome Outcome,
    PinnedApp? Item,
    string? ExistingId,
    string? Error)
{
    public static PinnedAppAddResult Added(PinnedApp item) => new(PinnedAppAddOutcome.Added, item, null, null);

    public static PinnedAppAddResult Duplicate(PinnedApp existing) =>
        new(PinnedAppAddOutcome.Duplicate, null, existing.Id, null);

    public static PinnedAppAddResult LimitReached(int maximum) =>
        new(PinnedAppAddOutcome.LimitReached, null, null, $"The dock holds at most {maximum} pinned apps.");

    public static PinnedAppAddResult Unsupported(string error) =>
        new(PinnedAppAddOutcome.Unsupported, null, null, error);

    public bool Succeeded => Outcome == PinnedAppAddOutcome.Added;
}

/// <summary>
/// The pinned list as a value: every operation returns the list it would become, so what the dock
/// shows is decided by pure rules and the caller owns when to persist. Nothing here touches the file
/// system, which is what lets the same rules be tested without a machine to run them on.
/// </summary>
public static class PinnedApps
{
    /// <summary>
    /// How many applications the zone holds. The dock is a fixed strip rather than a taskbar: past
    /// this many it stops accepting rather than growing across the screen, and an overflow menu is a
    /// later phase's job.
    /// </summary>
    public const int MaximumCount = 12;

    /// <summary>
    /// Adds <paramref name="candidate"/> unless the same application is already pinned or the dock is
    /// full. The duplicate check comes first, because "you already pinned this" is the more useful
    /// answer when the dock happens to be full as well.
    /// </summary>
    public static PinnedAppAddResult Add(
        IReadOnlyList<PinnedApp> items,
        PinnedApp candidate,
        int maximum = MaximumCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(candidate);

        var existing = FindByIdentity(items, candidate.Identity);
        if (existing is not null)
        {
            return PinnedAppAddResult.Duplicate(existing);
        }

        return items.Count >= maximum
            ? PinnedAppAddResult.LimitReached(maximum)
            : PinnedAppAddResult.Added(candidate);
    }

    /// <summary>The pinned app with this identity, or null when the application is not pinned.</summary>
    public static PinnedApp? FindByIdentity(IReadOnlyList<PinnedApp> items, string identity)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items.FirstOrDefault(item =>
            string.Equals(item.Identity, identity, StringComparison.OrdinalIgnoreCase));
    }

    public static PinnedApp? Find(IReadOnlyList<PinnedApp> items, string id)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The list without the app, or the same list when the app is not in it.</summary>
    public static IReadOnlyList<PinnedApp> Remove(IReadOnlyList<PinnedApp> items, string id)
    {
        ArgumentNullException.ThrowIfNull(items);

        var remaining = items
            .Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return remaining.Length == items.Count ? items : remaining;
    }

    /// <summary>
    /// The list with the app at the place the pointer chose. The insertion index is counted among the
    /// other items, which is the same index the drag preview used, so the item lands where the gap
    /// was drawn.
    /// </summary>
    public static IReadOnlyList<PinnedApp> Move(IReadOnlyList<PinnedApp> items, string id, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        var from = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                from = i;
                break;
            }
        }

        if (from < 0)
        {
            return items;
        }

        var order = DockReorder.OrderAfterDrop(items.Count, from, targetIndex);
        return order.Select(index => items[index]).ToArray();
    }
}
