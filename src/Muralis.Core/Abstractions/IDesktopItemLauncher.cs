using Muralis.Core.Desktop;

namespace Muralis.Core.Abstractions;

/// <summary>How an attempt to open a desktop item ended.</summary>
public enum DesktopItemLaunchOutcome
{
    /// <summary>The shell took the request.</summary>
    Launched,

    /// <summary>The target is gone; the item stays and is marked missing.</summary>
    Missing,

    /// <summary>The shell refused, or could not start what it points at.</summary>
    Failed,
}

/// <summary>The result of one launch attempt, with the shell's own words when it failed.</summary>
public sealed record DesktopItemLaunchResult(DesktopItemLaunchOutcome Outcome, string? Error)
{
    public static DesktopItemLaunchResult Launched { get; } = new(DesktopItemLaunchOutcome.Launched, null);

    public static DesktopItemLaunchResult Missing(string location) =>
        new(DesktopItemLaunchOutcome.Missing, location);

    public static DesktopItemLaunchResult Failed(string error) =>
        new(DesktopItemLaunchOutcome.Failed, error);
}

/// <summary>
/// Opens what a desktop item points at, the way the shell would open it from Explorer: a program
/// runs, a document opens in its associated program, a folder opens in the shell and an address
/// opens in the default browser. Implemented per platform so callers never build command lines or
/// start processes themselves, and so every launch in the app has one place to be logged and
/// rate-limited from.
/// </summary>
public interface IDesktopItemLauncher
{
    /// <summary>
    /// Opens <paramref name="item"/>. Never throws for a bad target: a missing one comes back as
    /// <see cref="DesktopItemLaunchOutcome.Missing"/> so the caller can mark the item and carry on.
    /// </summary>
    Task<DesktopItemLaunchResult> LaunchAsync(DesktopItem item, CancellationToken cancellationToken = default);
}
