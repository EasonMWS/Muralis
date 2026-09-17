namespace Muralis.Core.Abstractions;

/// <summary>
/// Lifecycle boundary for Clean Desktop. Implementations may only change the visibility of Explorer's
/// desktop icons and mount an alternative presentation; they must never move, delete, rename, enumerate
/// as owner, or otherwise take ownership of the user's desktop files, and must never kill Explorer.
/// </summary>
public interface ICleanDesktopPresentation
{
    /// <summary>True only when both reversible icon visibility and the replacement Shelf host are ready.</summary>
    bool IsAvailable { get; }

    /// <summary>True when this process currently intends Explorer's desktop icons to be hidden.</summary>
    bool IsNativeDesktopHidden { get; }

    /// <summary>Activates the alternative presentation after its full lifecycle is ready.</summary>
    Task<CleanDesktopPresentationResult> ActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores Explorer's icons and removes the alternative presentation.</summary>
    Task<CleanDesktopPresentationResult> DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Reacquires Explorer's current desktop view and makes reality match the active state.</summary>
    Task<CleanDesktopPresentationResult> SynchronizeStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup safety net. If the previous process left a marker, native icons are restored and the
    /// marker is cleared before a persisted desktop mode is considered.
    /// </summary>
    Task<CleanDesktopPresentationResult> RecoverIfNeededAsync(CancellationToken cancellationToken = default);
}

public sealed record CleanDesktopPresentationResult(bool IsActive, string? Error = null)
{
    public static CleanDesktopPresentationResult Active { get; } = new(true);

    public static CleanDesktopPresentationResult Inactive { get; } = new(false);
}
