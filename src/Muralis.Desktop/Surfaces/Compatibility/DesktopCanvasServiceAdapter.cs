using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Models;
using Muralis.Desktop.Input;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Surfaces.Compatibility;

/// <summary>
/// Puts the desktop canvas prototype on the primary display and takes it off again. The layout is
/// read from its own prototype file every time the canvas is enabled, so a reset to the seed layout
/// or a hand-edit works without restarting the app; dragging is persisted by the canvas itself.
/// Switching the canvas off removes the surface and releases its window — nothing about the native
/// desktop is ever modified, so the desktop comes back exactly as it was.
/// </summary>
/// <remarks>
/// A thin adapter in the house style: no thread, no window and no WorkerW lookup live here. The
/// shell owns the desktop layer and <see cref="CanvasSurfaceContent"/> owns the visuals and input,
/// including survival across Explorer restarts, which the shell turns into a fresh mount. The
/// pointer router is handed to the canvas on creation and reads its stats for the overlay.
/// </remarks>
public sealed class DesktopCanvasServiceAdapter : IDesktopCanvasService
{
    private readonly ILogger<DesktopCanvasServiceAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDesktopShell _shell;
    private readonly DesktopLayoutStore _store;

    private readonly DesktopPointerRouter _pointer;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private CanvasSurfaceContent? _content;
    private IDesktopSurface? _surface;
    private CanvasPrototypeStatus _status = CanvasPrototypeStatus.Disabled;

    public DesktopCanvasServiceAdapter(
        ILoggerFactory loggerFactory,
        IDesktopShell shell,
        DesktopLayoutStore store,
        DesktopPointerRouter pointer)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pointer);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DesktopCanvasServiceAdapter>();
        _shell = shell;
        _store = store;
        _pointer = pointer;
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
            var content = new CanvasSurfaceContent(layout, _store, _loggerFactory.CreateLogger<CanvasSurfaceContent>(), _pointer);
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
