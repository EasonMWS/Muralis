using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// Pure planning logic for the wallpaper rotation: decides which wallpaper comes next.
/// Kept free of timers and IO so it can be tested directly.
/// </summary>
public static class RotationPlanner
{
    /// <summary>
    /// Picks a random wallpaper, avoiding the one that is currently applied when other
    /// candidates exist. Returns null for an empty candidate list.
    /// </summary>
    public static Wallpaper? PickNext(IReadOnlyList<Wallpaper> candidates, string? currentWallpaperId, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return null;
        }

        var usable = candidates
            .Where(item => item.HasLocalFile)
            .ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        if (usable.Count == 1)
        {
            return usable[0];
        }

        var pool = usable
            .Where(item => !string.Equals(item.Id, currentWallpaperId, StringComparison.Ordinal))
            .ToList();

        if (pool.Count == 0)
        {
            pool = usable;
        }

        return pool[(random ?? Random.Shared).Next(pool.Count)];
    }

    public static TimeSpan ToTimeSpan(this RotationInterval interval) =>
        TimeSpan.FromMinutes((int)interval);
}
