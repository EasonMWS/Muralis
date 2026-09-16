using Muralis.Core.Desktop;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Brings the items already on the user's own desktop onto the canvas. It only ever reads: each entry
/// becomes an item that points at where the file already is, and nothing about the desktop, its files
/// or their attributes is written to, so a sync cannot lose anything the user put there.
/// </summary>
/// <remarks>
/// <para>
/// A scan is a listing per desktop folder and nothing more, so it happens when it is asked for — when
/// a takeover is switched on, when the app starts, and when the user asks for one — and never on a
/// timer. Watching, when it is on at all, is a file system watch with a debounce, which is the only
/// way to notice a change without either polling or missing it.
/// </para>
/// <para>
/// The same file is never adopted twice: an item remembers the path it came from, and an entry the
/// user has taken off the canvas is turned down in the layout, so a later sync cannot undo a removal
/// the user made on purpose.
/// </para>
/// </remarks>
public interface IDesktopItemSyncService
{
    /// <summary>Whether the user's own desktop is being watched for changes right now.</summary>
    bool IsWatching { get; }

    /// <summary>
    /// What a sync would do right now, without changing anything: which entries would be added, which
    /// are on the canvas already, which the user turned down, and which cannot be shown at all. This is
    /// what a first run shows before it adopts anything.
    /// </summary>
    Task<DesktopAdoptionPlan> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adopts what the user's own desktop holds and the canvas does not yet, and reports what it added.
    /// </summary>
    Task<DesktopAdoptionResult> SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts noticing changes to the desktop folders and syncing after them, once they have settled.
    /// Safe to call when it is already watching.
    /// </summary>
    void StartWatching();

    /// <summary>Stops noticing changes. Safe to call when it is not watching.</summary>
    void StopWatching();
}
