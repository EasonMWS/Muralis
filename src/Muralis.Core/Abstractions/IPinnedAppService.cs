using Muralis.Core.DockShell;

namespace Muralis.Core.Abstractions;

/// <summary>
/// The applications pinned to the dock, in the order the user put them in. The list is the whole
/// truth: what the dock draws, in what order, and what a launch starts all come from here, so the UI
/// never keeps a second copy that could disagree with what was saved.
/// </summary>
/// <remarks>
/// Pinning and unpinning only ever change this list. The file an application lives in is never
/// touched, moved or deleted by anything on this interface, and a pin whose target has been deleted
/// stays in the list and is reported as unavailable rather than being quietly removed.
/// </remarks>
public interface IPinnedAppService
{
    /// <summary>The pinned applications, in dock order.</summary>
    IReadOnlyList<PinnedApp> Items { get; }

    /// <summary>How many applications the zone accepts.</summary>
    int MaximumCount { get; }

    /// <summary>Raised whenever the list changes. May be raised on a background thread.</summary>
    event EventHandler<IReadOnlyList<PinnedApp>>? Changed;

    /// <summary>Reads the saved pins back. The call a fresh process makes once, before the dock is drawn.</summary>
    Task<IReadOnlyList<PinnedApp>> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pins the application at <paramref name="path"/>. The result says which of the four things
    /// happened; the caller is expected to tell the user, because "you already pinned this" is worth
    /// saying and only the caller can point at the pin that already exists.
    /// </summary>
    Task<PinnedAppAddResult> AddAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Unpins the application. The file it points at is untouched.</summary>
    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Moves a pinned application to the place the pointer chose.</summary>
    Task<IReadOnlyList<PinnedApp>> MoveAsync(
        string id,
        int targetIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a pinned application, remembering the outcome in the log.</summary>
    Task<ApplicationLaunchResult> LaunchAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Whether the thing this pin points at is still there. Never launches anything.</summary>
    bool IsAvailable(PinnedApp app);
}
