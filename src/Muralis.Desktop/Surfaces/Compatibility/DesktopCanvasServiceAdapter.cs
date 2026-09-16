using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Dock;
using Muralis.Core.Models;
using Muralis.Desktop.Input;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Surfaces.Compatibility;

/// <summary>
/// Puts the desktop canvas on the primary display and takes it off again, and is the app's way in to
/// the items on it: the list the page draws, an item the user just imported, and a removal from the
/// canvas. The layout is read from its own file every time the canvas is enabled, so a hand-edit
/// works without restarting the app; dragging is persisted by the canvas itself. Switching the
/// canvas off removes the surface and releases its window — nothing about the native desktop is ever
/// modified, so the desktop comes back exactly as it was.
/// </summary>
/// <remarks>
/// A thin adapter in the house style: no thread, no window and no WorkerW lookup live here. The
/// shell owns the desktop layer and <see cref="CanvasSurfaceContent"/> owns the visuals and input,
/// including survival across Explorer restarts, which the shell turns into a fresh mount. The
/// pointer router and the launcher are handed to the canvas on creation: the router reads the
/// pointer across the whole desktop, and a double-click is opened by the launcher, never here.
/// </remarks>
public sealed class DesktopCanvasServiceAdapter : IDesktopCanvasService
{
    private readonly ILogger<DesktopCanvasServiceAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDesktopShell _shell;
    private readonly DesktopLayoutStore _store;

    private readonly DesktopPointerRouter _pointer;
    private readonly IDesktopItemLauncher _launcher;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private CanvasSurfaceContent? _content;
    private IDesktopSurface? _surface;
    private CanvasPrototypeStatus _status = CanvasPrototypeStatus.Disabled;

    public DesktopCanvasServiceAdapter(
        ILoggerFactory loggerFactory,
        IDesktopShell shell,
        DesktopLayoutStore store,
        DesktopPointerRouter pointer,
        IDesktopItemLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(launcher);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DesktopCanvasServiceAdapter>();
        _shell = shell;
        _store = store;
        _pointer = pointer;
        _launcher = launcher;
    }

    public CanvasPrototypeStatus Status => Volatile.Read(ref _status);

    public event EventHandler<CanvasPrototypeStatus>? StatusChanged;

    public CanvasDiagnosticsSnapshot? Diagnostics => _content?.Snapshot() is { } snapshot
        ? snapshot with
        {
            MonitorId = _shell.Monitors.Primary?.Runtime.FriendlyName,
            SurfaceState = _shell.State.ToString(),
            PointerContext = _pointer.Stats.Context.ToString(),
            PointerDispatchesPerSecond = _pointer.Stats.DispatchesPerSecond,
            PointerReports = _pointer.Stats.Reports,
            PointerDispatches = _pointer.Stats.Dispatches,
        }
        : null;

    public async Task<CanvasPrototypeStatus> EnableAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_content is not null)
            {
                return Status;
            }

            Publish(new CanvasPrototypeStatus(CanvasPrototypeState.Starting));

            var primary = _shell.Monitors.Primary;
            if (primary is null)
            {
                return Fail("No display was found to put the canvas on.");
            }

            var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var content = new CanvasSurfaceContent(
                layout,
                _store,
                _loggerFactory.CreateLogger<CanvasSurfaceContent>(),
                _pointer,
                _launcher);
            _content = content;

            CanvasPrototypeStatus status;
            try
            {
                _surface = await _shell
                    .AddSurfaceAsync(new SurfaceRequest(content, MonitorRef.From(primary)), cancellationToken)
                    .ConfigureAwait(false);

                status = new CanvasPrototypeStatus(CanvasPrototypeState.Active, layout.Items.Count);
                _logger.LogInformation("The desktop canvas is showing {Count} items", layout.Items.Count);
            }
            catch (OperationCanceledException)
            {
                _content = null;
                _surface = null;
                Publish(CanvasPrototypeStatus.Disabled);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The desktop canvas could not be shown");
                _content = null;
                _surface = null;
                status = new CanvasPrototypeStatus(CanvasPrototypeState.Failed, 0, ex.Message);
            }

            Publish(status);
            return status;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var surface = _surface;
            _content = null;
            _surface = null;

            if (surface is not null)
            {
                // Releases the mount, the composition target and the window with it; the desktop
                // was never modified, so there is nothing else to undo.
                await _shell.RemoveSurfaceAsync(surface, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("The desktop canvas was removed from the desktop");
            }

            Publish(CanvasPrototypeStatus.Disabled);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<DesktopItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        // The mounted canvas is the answer while it is showing — it owns the live layout — and with
        // nothing mounted, the answer is what the next mount would show, read from the layout file.
        // Read without the mutex: a stale answer is a list from a moment ago, never a torn one.
        if (_content is { } content && content.Items.Count > 0)
        {
            return content.Items;
        }

        var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. layout.Items.Select(item => item.Clone())];
    }

    public async Task<bool> AddItemAsync(DesktopItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_content is { } content)
            {
                content.AddItem(item);
                _logger.LogInformation("The item {Id} ({Name}) was added to the desktop canvas", item.Id, item.Name);
                return true;
            }

            // Nothing is mounted: the layout is edited on disk, so the item is already there when
            // the canvas is switched on next.
            var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (layout.Items.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
            {
                return false;
            }

            layout.Items.Add(item);
            await _store.SaveAsync(layout).ConfigureAwait(false);
            _logger.LogInformation("The item {Id} ({Name}) was added to the saved layout", item.Id, item.Name);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> RemoveItemAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_content is { } content)
            {
                // Only the layout is edited: the file, shortcut or folder behind the item is never
                // touched, so the user's own desktop and files stay exactly as they were.
                content.RemoveItem(id);
                _logger.LogInformation("The item {Id} was removed from the desktop canvas", id);
                return true;
            }

            var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var item = layout.Items.FirstOrDefault(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
            if (item is null)
            {
                return false;
            }

            layout.Items.Remove(item);
            await _store.SaveAsync(layout).ConfigureAwait(false);
            _logger.LogInformation("The item {Id} was removed from the saved layout", id);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<DockOptions> GetDockAsync(CancellationToken cancellationToken = default)
    {
        var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return layout.Dock.Clone();
    }

    public async Task<bool> UpdateDockAsync(DockOptions dock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dock);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An entry that names an item the layout does not hold would make the whole document
            // invalid, and the next load would set it aside. Nothing else in the dock is touched.
            if (_content is { } content)
            {
                var held = content.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                dock.Entries.RemoveAll(entry => entry is null || !held.Contains(entry.ItemId));
                content.UpdateDockOptions(dock);
                _logger.LogInformation(
                    "The desktop dock is now on the {Edge} edge{State} with {Count} items",
                    dock.Edge,
                    dock.Enabled ? string.Empty : " and switched off",
                    dock.Entries.Count);
                return true;
            }

            var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            dock.Entries.RemoveAll(entry => entry is null || !layout.Items.Any(item => string.Equals(item.Id, entry.ItemId, StringComparison.Ordinal)));
            layout.Dock.CopyFrom(dock);
            var problems = layout.Validate();
            if (problems.Count > 0)
            {
                _logger.LogWarning("The dock change was refused ({Problems})", string.Join(" ", problems));
                return false;
            }

            await _store.SaveAsync(layout).ConfigureAwait(false);
            _logger.LogInformation(
                "The saved desktop dock is now on the {Edge} edge{State} with {Count} items",
                dock.Edge,
                dock.Enabled ? string.Empty : " and switched off",
                dock.Entries.Count);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private CanvasPrototypeStatus Fail(string error)
    {
        _logger.LogWarning("The desktop canvas could not be shown: {Error}", error);
        var status = new CanvasPrototypeStatus(CanvasPrototypeState.Failed, 0, error);
        Publish(status);
        return status;
    }

    private void Publish(CanvasPrototypeStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
