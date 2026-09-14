namespace Muralis.Core.Models;

/// <summary>A wallpaper that was applied to the desktop, kept for the "recently used" feed.</summary>
public sealed class HistoryEntry
{
    public required string WallpaperId { get; init; }

    /// <summary>Denormalized title so history stays readable even if the record is removed.</summary>
    public string Title { get; init; } = string.Empty;

    public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.Now;

    public string? MonitorName { get; init; }
}
