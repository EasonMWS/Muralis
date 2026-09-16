using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;

namespace Muralis.Desktop.Sync;

/// <summary>
/// Reads the user's own desktop and brings what is on it onto the canvas. It only ever reads: an entry
/// becomes an item that points at where the file already is, and nothing about the desktop, its files
/// or their attributes is written to, so a sync can never lose anything the user put there.
/// </summary>
/// <remarks>
/// <para>
/// A sync happens when it is asked for — when a takeover is switched on, when the app starts, and when
/// the user asks for one — and never on a timer: a scan is one directory listing per desktop folder, and
/// there is no reason to pay for one per frame or per second. Watching, when it is on at all, is a file
/// system watch whose events are collapsed into one sync, because a single drag of a file can produce a
/// dozen of them.
/// </para>
/// <para>
/// The scan runs off the caller's thread: it touches the file system, and the desktop folders can be a
/// network path that takes a moment to answer. The adoption itself is handed to the canvas service,
/// which is the only thing that may edit the layout.
/// </para>
/// </remarks>
public sealed class DesktopItemSyncService : IDesktopItemSyncService, IDisposable
{
    /// <summary>
    /// How long the desktop has to be still before a change is taken up. Copying a file in produces a
    /// burst of events, and syncing after each one would adopt half-written entries and read the same
    /// folder again and again.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly ILogger<DesktopItemSyncService> _logger;
    private readonly DesktopContentScanner _scanner;
    private readonly DesktopLayoutStore _store;
    private readonly IDesktopCanvasService _canvas;

    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _settle;
    private bool _syncing;
    private bool _disposed;

    public DesktopItemSyncService(
        ILogger<DesktopItemSyncService> logger,
        DesktopContentScanner scanner,
        DesktopLayoutStore store,
        IDesktopCanvasService canvas)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(canvas);

        _logger = logger;
        _scanner = scanner;
        _store = store;
        _canvas = canvas;
    }

    /// <inheritdoc />
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public async Task<DesktopAdoptionPlan> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var scan = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var plan = DesktopContentAdopter.Plan(scan, layout);

        _logger.LogInformation(
            "The desktop holds {Adoptable} entries that could be shown, {Unsupported} that could not; {Already} are on the canvas and {Declined} were turned down",
            plan.ToAdopt.Count,
            plan.Unsupported.Count,
            plan.AlreadyAdopted.Count,
            plan.Declined.Count);

        foreach (var skipped in plan.Unsupported)
        {
            _logger.LogDebug("The desktop entry {Name} was left out: {Reason}", skipped.Name, skipped.Reason);
        }

        return plan;
    }

    /// <inheritdoc />
    public async Task<DesktopAdoptionResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plan = await PreviewAsync(cancellationToken).ConfigureAwait(false);
        if (plan.ToAdopt.Count == 0)
        {
            return DesktopAdoptionResult.None;
        }

        // The desktop was still read and the plan still says what is on it — that is what the preview is
        // for — but the choice not to bring any of it across is the user's, and it is answered here
        // rather than by the canvas, because this is the one command that adopts anything.
        var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!layout.Takeover.AdoptDesktopItems)
        {
            _logger.LogInformation(
                "The desktop holds {Count} entries that could be shown, and none of them was adopted: the layout says not to",
                plan.ToAdopt.Count);
            return DesktopAdoptionResult.None;
        }

        return await _canvas.AdoptAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<FileSystemWatcher> started;

        lock (_gate)
        {
            if (_watchers.Count > 0)
            {
                return;
            }

            // One watcher per folder, because a file system watch is bound to one path. A folder that
            // cannot be watched is simply not watched: the next refresh still reads it, so nothing about
            // the desktop is lost by it.
            foreach (var folder in _scanner.Folders)
            {
                if (!TryWatch(folder, out var watcher))
                {
                    continue;
                }

                _watchers.Add(watcher);
            }

            started = [.. _watchers];
        }

        if (started.Count == 0)
        {
            _logger.LogInformation("The desktop folders cannot be watched; the canvas will sync when it is asked to");
            return;
        }

        _logger.LogInformation("The desktop is being watched for changes in {Count} folders", started.Count);
    }

    /// <inheritdoc />
    public void StopWatching()
    {
        List<FileSystemWatcher> watchers;
        Timer? settle;

        lock (_gate)
        {
            watchers = [.. _watchers];
            settle = _settle;
            _watchers.Clear();
            _settle = null;
        }

        foreach (var watcher in watchers)
        {
            // The watch is switched off before it is disposed: an event that is already on its way must
            // not arm a timer for a watch that is being taken down.
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnDesktopChanged;
            watcher.Created -= OnDesktopChanged;
            watcher.Deleted -= OnDesktopChanged;
            watcher.Renamed -= OnDesktopChanged;
            watcher.Dispose();
        }

        settle?.Dispose();

        if (watchers.Count > 0)
        {
            _logger.LogInformation("The desktop is no longer being watched");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopWatching();
    }

    /// <summary>
    /// A change was noticed. The watcher raises these on its own thread and can raise a great many of
    /// them at once, so the sync is not started here: a timer is armed instead, and each new event
    /// pushes it back. The desktop only has to stop changing for the delay before one sync runs.
    /// </summary>
    private void OnDesktopChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _watchers.Count == 0)
            {
                return;
            }

            _settle ??= new Timer(_ => SettleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async void SettleAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            // One sync at a time: a second change that arrives while one is running only has to wait for
            // the next settle, and running them on top of each other could adopt the same file twice.
            lock (_gate)
            {
                if (_syncing)
                {
                    return;
                }

                _syncing = true;
            }

            try
            {
                var adopted = await SyncAsync().ConfigureAwait(false);
                if (adopted.Added.Count > 0)
                {
                    _logger.LogInformation("The desktop changed: {Count} items were brought across", adopted.Added.Count);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _syncing = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this: it runs from a timer, and a desktop that could not be read must not
            // take the app down with it.
            _logger.LogWarning(ex, "The desktop could not be synced after it changed");
        }
    }

    /// <summary>
    /// Starts one watcher on one folder. The events are collapsed by the timer, so what is noticed here
    /// is only ever "something about this folder changed".
    /// </summary>
    private bool TryWatch(string folder, out FileSystemWatcher watcher)
    {
        watcher = null!;

        try
        {
            if (!Directory.Exists(folder))
            {
                return false;
            }

            var created = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };

            created.Changed += OnDesktopChanged;
            created.Created += OnDesktopChanged;
            created.Deleted += OnDesktopChanged;
            created.Renamed += OnDesktopChanged;
            created.EnableRaisingEvents = true;

            watcher = created;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "The desktop folder {Folder} could not be watched", folder);
            return false;
        }
    }
}
