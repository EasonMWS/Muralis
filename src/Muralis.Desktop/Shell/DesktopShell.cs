using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Input;
using Muralis.Desktop.Interop;
using Muralis.Desktop.Monitors;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Shell;

/// <summary>
/// The one desktop shell of the process: it owns the shell thread, the display snapshot, the
/// wallpaper worker and every surface mounted on the desktop. Content is registered through
/// <see cref="AddSurfaceAsync"/> and lives until it is removed. When Explorer restarts, the shell
/// notices, rejects the dead mounts, re-discovers the desktop layer and re-mounts — content never
/// sees shell lifecycle events, it is simply mounted again.
/// </summary>
/// <remarks>
/// Threads: everything native — the wallpaper worker, window creation, positioning, the mount
/// registry — happens on the shell thread, which also pumps the surfaces' window messages. Other
/// threads only queue work through <see cref="AddSurfaceAsync"/> / <see cref="RemoveSurfaceAsync"/>
/// and await the result. Content may render on threads of its own (the video wallpaper presents
/// frames on the media player's thread); the shell never touches those.
/// </remarks>
public sealed class DesktopShell : IDesktopShell, IDisposable
{
    private readonly ILogger<DesktopShell> _logger;
    private readonly ShellEventSource _shellEvents;
    private readonly DesktopPointerRouter _pointer;
    private readonly MonitorManager _monitors = new();
    private readonly DesktopLayerHost _layerHost;
    private readonly Win32SurfaceHost _surfaceHost;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<DesktopSurfaceId, DesktopSurface> _surfaces = [];

    // Guarded by _lifecycleGate; only ever written by the shell thread itself.
    private Thread? _thread;
    private uint _threadId;
    private bool _acceptingWork;

    private volatile DesktopShellState _state = DesktopShellState.Stopped;
    private bool _disposed;

    public DesktopShell(ILoggerFactory loggerFactory, ShellEventSource shellEvents, DesktopPointerRouter pointer)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(shellEvents);
        ArgumentNullException.ThrowIfNull(pointer);

        _logger = loggerFactory.CreateLogger<DesktopShell>();
        _shellEvents = shellEvents;
        _pointer = pointer;
        _layerHost = new DesktopLayerHost(loggerFactory.CreateLogger<DesktopLayerHost>());
        _surfaceHost = new Win32SurfaceHost(loggerFactory.CreateLogger<Win32SurfaceHost>(), OnSurfaceWindowLost);

        // The snapshot must exist before the first surface is added, so the first read happens here
        // instead of on the shell thread. Later refreshes run there.
        RefreshMonitors();

        _shellEvents.ShellRestarted += OnShellRestartedAnnounced;
        _shellEvents.DisplaysChanged += OnDisplaysChangedAnnounced;
    }

    public DesktopShellState State => _state;

    public IMonitorManager Monitors => _monitors;

    public event EventHandler<DesktopShellState>? StateChanged;

    public event EventHandler? ShellRestarted;

    public async Task<IDesktopSurface> AddSurfaceAsync(SurfaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var problems = request.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(request));
        }

        var completion = new TaskCompletionSource<IDesktopSurface>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostWork(() => MountNewSurface(request, completion));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveSurfaceAsync(IDesktopSurface surface, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface is not DesktopSurface desktopSurface)
        {
            throw new ArgumentException("The surface was not created by this shell.", nameof(surface));
        }

        if (_disposed)
        {
            // Disposing the shell released every surface already.
            return;
        }

        Thread? stoppingThread = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PostWork(() =>
        {
            try
            {
                RemoveSurface(desktopSurface);

                if (_surfaces.Count == 0 && _work.IsEmpty)
                {
                    // This was the last surface: the shell releases its thread right after this
                    // drain, so the caller can wait for the desktop layer to be fully handed back.
                    lock (_lifecycleGate)
                    {
                        stoppingThread = _thread;
                    }
                }

                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (stoppingThread is not null)
        {
            await JoinThreadAsync(stoppingThread).ConfigureAwait(false);
        }
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shellEvents.ShellRestarted -= OnShellRestartedAnnounced;
        _shellEvents.DisplaysChanged -= OnDisplaysChangedAnnounced;

        Thread? thread;
        lock (_lifecycleGate)
        {
            thread = _thread;
        }

        if (thread is null)
        {
            return;
        }

        var released = new ManualResetEventSlim(false);
        PostWork(() =>
        {
            try
            {
                StopFromInside();
            }
            finally
            {
                released.Set();
            }
        });

        // Waited for on the pool: the caller is usually the UI thread, which must not be held while
        // the desktop layer releases its windows.
        await Task.Run(() =>
        {
            if (!released.Wait(TimeSpan.FromSeconds(10)))
            {
                _logger.LogWarning("The desktop shell did not release its surfaces in time");
            }

            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                _logger.LogWarning("The desktop shell thread did not end in time");
            }

            released.Dispose();
        }).ConfigureAwait(false);
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Queues a work item and makes sure a shell thread is there to run it. Work items always run on
    /// that thread, one at a time, so nothing else needs to be locked.
    /// </summary>
    private void PostWork(Action work)
    {
        _work.Enqueue(work);

        Thread? starting = null;
        uint wake = 0;

        lock (_lifecycleGate)
        {
            if (_thread is { IsAlive: true } && _acceptingWork)
            {
                wake = _threadId;
            }
            else
            {
                starting = new Thread(Run) { Name = "Muralis desktop shell", IsBackground = true };
                _thread = starting;
                _threadId = 0;
                _acceptingWork = true;
            }
        }

        if (starting is not null)
        {
            starting.Start();
            SetState(DesktopShellState.Starting);
            return;
        }

        // A thread that is still coming up drains the queue itself; a post that fails for any other
        // reason is picked up by the next wake-up.
        if (wake != 0 && !NativeMethods.PostThreadMessageW(wake, NativeMethods.WmRunWork, nint.Zero, nint.Zero))
        {
            _logger.LogWarning("The desktop shell could not be woken to run work ({Error})", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Queues work only while the shell is actually running; without a thread there is nothing to work on.</summary>
    private void PostWorkIfRunning(Action work)
    {
        lock (_lifecycleGate)
        {
            if (_thread is not { IsAlive: true } || !_acceptingWork)
            {
                return;
            }
        }

        PostWork(work);
    }

    private void Run()
    {
        try
        {
            // Force the message queue into existence before the thread id is published: a
            // PostThreadMessageW to a thread that has no queue yet is silently lost.
            NativeMethods.PeekMessageW(out _, nint.Zero, 0, 0, NativeMethods.PmNoRemove);

            lock (_lifecycleGate)
            {
                _threadId = NativeMethods.GetCurrentThreadId();
            }

            // A thread timer keeps ticking even after the surfaces' windows were destroyed by an
            // Explorer restart; a window timer would die with them and the re-mount would never run.
            NativeMethods.SetTimer(
                nint.Zero,
                NativeMethods.DisplayChangeCheckTimerId,
                NativeMethods.DisplayChangeCheckIntervalMs,
                nint.Zero);

            // The pointer router's hidden window belongs to this thread, so it starts and ends with
            // the thread; the router object itself outlives shell thread restarts.
            _pointer.Attach();

            SetState(DesktopShellState.Running);

            // Work queued while the thread was coming up is drained by this first wake-up.
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmRunWork, nint.Zero, nint.Zero);

            RunMessageLoop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop shell thread failed");
        }
        finally
        {
            FinishThread();
        }
    }

    private void RunMessageLoop()
    {
        while (true)
        {
            var result = NativeMethods.GetMessageW(out var message, nint.Zero, 0, 0);
            if (result <= 0)
            {
                return;
            }

            // Thread messages (work, timer, stop, shell restart) arrive with no window at all; they
            // are handled here because the surfaces' windows may already be gone.
            if (message.Hwnd == nint.Zero)
            {
                switch (message.Value)
                {
                    case NativeMethods.WmRunWork:
                        DrainWork();
                        break;

                    case NativeMethods.WmTimer:
                        // The drain also picks up work whose wake-up message could not be posted.
                        DrainWork();
                        CheckDesktopState();
                        break;

                    case NativeMethods.WmShellRestarted:
                        OnShellRestarted();
                        break;

                    case NativeMethods.WmStopHost:
                        StopFromInside();
                        break;
                }

                StopIfIdle();
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A desktop shell work item failed");
            }
        }
    }

    /// <summary>Ends the shell thread once nothing is mounted and nothing is queued any more.</summary>
    private void StopIfIdle()
    {
        if (_surfaces.Count > 0 || !_work.IsEmpty)
        {
            return;
        }

        StopLoop();
    }

    private void StopLoop()
    {
        bool stopping;
        lock (_lifecycleGate)
        {
            stopping = _acceptingWork;
            _acceptingWork = false;
        }

        if (!stopping)
        {
            return;
        }

        NativeMethods.KillTimer(nint.Zero, NativeMethods.DisplayChangeCheckTimerId);
        NativeMethods.PostQuitMessage(0);
        SetState(DesktopShellState.Stopping);
    }

    private void FinishThread()
    {
        bool stopped = false;
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_thread, Thread.CurrentThread))
            {
                _thread = null;
                _threadId = 0;
                _acceptingWork = false;
                stopped = true;
            }
        }

        if (!stopped)
        {
            return;
        }

        // The router's window lives on the thread that is ending; it has to go before the thread does.
        _pointer.Detach();

        SetState(DesktopShellState.Stopped);

        // Work that arrived after the loop's last drain would otherwise wait forever; a fresh
        // thread runs it (and stops again when it turns out to be idle).
        if (!_work.IsEmpty)
        {
            PostWork(static () => { });
        }
    }

    /// <summary>Releases every surface and ends the loop. Runs on the shell thread.</summary>
    private void StopFromInside()
    {
        foreach (var surface in _surfaces.Values.ToArray())
        {
            try
            {
                _surfaceHost.DestroyAsync(surface).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A desktop surface could not be released during shutdown");
            }
        }

        _surfaces.Clear();
        StopLoop();
    }

    private void MountNewSurface(SurfaceRequest request, TaskCompletionSource<IDesktopSurface> completion)
    {
        try
        {
            if (FindSurfaceFor(request.Monitor, request.Content.Kind) is not null)
            {
                throw new InvalidOperationException($"The display {request.Monitor.StableId} already shows this kind of surface.");
            }

            RefreshMonitors();
            var monitor = _monitors.Resolve(request.Monitor)
                ?? throw new InvalidOperationException($"The display {request.Monitor.StableId} is not available.");

            var parent = EnsureParentFor(request.Content.Kind);
            if (parent == nint.Zero)
            {
                throw new InvalidOperationException(PlacementProblemFor(request.Content.Kind));
            }

            var surface = (DesktopSurface)_surfaceHost.CreateAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            _surfaces.Add(surface.Id, surface);

            try
            {
                Attach(surface, parent, monitor);
            }
            catch
            {
                _surfaces.Remove(surface.Id);
                throw;
            }

            completion.TrySetResult(surface);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop surface could not be mounted");
            completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// Creates the window and lets the content mount into it. Runs on the shell thread; the content
    /// is expected to finish its mount there, which is what lets the video wallpaper keep its media
    /// player on the thread that owns the window.
    /// </summary>
    private void Attach(DesktopSurface surface, nint parent, Monitor monitor)
    {
        var kind = surface.Content.Kind;
        var geometry = MonitorGeometry.From(monitor.Runtime);
        var window = _surfaceHost.CreateWindow(parent, kind, geometry);
        _surfaceHost.Track(window, surface);

        try
        {
            var target = Win32SurfaceTarget.ForWindow(window, geometry, monitor.Runtime.ScaleFactor, LayerFor(kind));
            surface.AttachAsync(target, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            _surfaceHost.DestroyWindow(window);
            throw;
        }
    }

    /// <summary>Which desktop window a surface of this kind is parented to.</summary>
    private nint EnsureParentFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.InteractiveOverlay => _layerHost.EnsureIconHost(),
        _ => _layerHost.EnsureWorker(),
    };

    private static SurfaceLayer LayerFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.InteractiveOverlay => SurfaceLayer.DesktopInteractiveLayer,
        _ => SurfaceLayer.WallpaperLayer,
    };

    private static string PlacementProblemFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.InteractiveOverlay => "The desktop icon host was not found; the surface cannot be placed above the icons.",
        _ => "The desktop worker window was not found; the surface cannot be placed behind the icons.",
    };

    private void RemoveSurface(DesktopSurface surface)
    {
        _surfaces.Remove(surface.Id);
        _surfaceHost.DestroyAsync(surface).GetAwaiter().GetResult();
    }

    /// <summary>Explorer restarted: the desktop layer is about to be (or already was) rebuilt.</summary>
    private void OnShellRestartedAnnounced(object? sender, EventArgs e)
    {
        _logger.LogInformation("Explorer restarted; re-mounting desktop content");
        ShellRestarted?.Invoke(this, EventArgs.Empty);

        PostWorkIfRunning(OnShellRestarted);
    }

    private void OnShellRestarted()
    {
        // The announcement can arrive before the old windows die; their destroy notifications, or
        // the watchdog, pick up the loss then.
        foreach (var surface in _surfaces.Values)
        {
            if (surface.State == SurfaceState.Mounted && !surface.IsWindowAlive)
            {
                Orphan(surface);
            }
        }

        CheckDesktopState();
    }

    private void OnDisplaysChangedAnnounced(object? sender, EventArgs e) => PostWorkIfRunning(CheckDesktopState);

    /// <summary>
    /// Brings the mounts back in line with reality: refreshes the display snapshot, re-mounts
    /// orphaned surfaces and keeps mounted ones positioned for their display.
    /// </summary>
    private void CheckDesktopState()
    {
        RefreshMonitors();
        UpdateMountedSurfaces();
    }

    private void UpdateMountedSurfaces()
    {
        var primary = _monitors.Primary;
        var monitorRef = primary is null ? (MonitorRef?)null : MonitorRef.From(primary);
        var geometry = primary is null ? (MonitorGeometry?)null : MonitorGeometry.From(primary.Runtime);

        foreach (var surface in _surfaces.Values)
        {
            switch (surface.State)
            {
                case SurfaceState.Mounted:
                    if (!surface.IsWindowAlive)
                    {
                        // Safety net: the destroy notification can be missed when Explorer takes the
                        // whole layer down at once.
                        Orphan(surface);
                        TryRemount(surface);
                    }
                    else
                    {
                        if (surface.Content.Kind == SurfaceKind.InteractiveOverlay)
                        {
                            // Explorer can reshuffle the icon host's children; interactive content
                            // has to stay above the icon view to remain visible and clickable.
                            _surfaceHost.BringToTop(surface.WindowHandle);
                        }

                        if (monitorRef is not null && geometry is not null
                            && surface.Monitor == monitorRef.Value && surface.Bounds != geometry.Value.Bounds)
                        {
                            surface.MoveTo(monitorRef.Value, geometry.Value);
                        }
                    }

                    break;

                case SurfaceState.Orphaned:
                    TryRemount(surface);
                    break;
            }
        }
    }

    /// <summary>
    /// The window died under a surface: its mount is released, the intent to show stays. Both the
    /// window procedure and the watchdog report the loss here, so it is only ever acted on once.
    /// </summary>
    private bool Orphan(DesktopSurface surface)
    {
        if (!surface.MarkOrphaned())
        {
            return false;
        }

        _logger.LogInformation("The desktop host window was lost (Explorer restart?); preparing to re-mount");
        return true;
    }

    private void OnSurfaceWindowLost(DesktopSurface surface)
    {
        // Runs on the shell thread, from the surface's window procedure. Re-mounting right away
        // means a rebuilt desktop layer is used without waiting for the next watchdog tick.
        if (Orphan(surface))
        {
            TryRemount(surface);
        }
    }

    /// <summary>
    /// Puts an orphaned surface back on the desktop layer, retrying until Explorer has rebuilt the
    /// wallpaper worker. Called by the watchdog and after every shell notification.
    /// </summary>
    private void TryRemount(DesktopSurface surface)
    {
        surface.RemountAttempts++;

        var parent = EnsureParentFor(surface.Content.Kind);
        if (parent == nint.Zero)
        {
            if (surface.RemountAttempts == 1)
            {
                _logger.LogWarning(
                    "The desktop layer is not back yet; retrying every {Seconds} s",
                    NativeMethods.DisplayChangeCheckIntervalMs / 1000);
            }

            return;
        }

        var monitor = _monitors.Primary;
        if (monitor is null)
        {
            _logger.LogWarning("No display is available to re-mount the desktop surface on");
            return;
        }

        var attempts = surface.RemountAttempts;
        try
        {
            Attach(surface, parent, monitor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-mounting the desktop surface failed (attempt {Attempt})", attempts);

            // Discard the half-built mount; the surface stays orphaned for the next attempt.
            surface.MarkOrphaned();
            return;
        }

        _logger.LogInformation("The desktop surface is back on the desktop after {Attempts} attempt(s)", attempts);
    }

    private DesktopSurface? FindSurfaceFor(MonitorRef monitor, SurfaceKind kind)
    {
        foreach (var surface in _surfaces.Values)
        {
            if (surface.Content.Kind == kind && surface.Monitor == monitor)
            {
                return surface;
            }
        }

        return null;
    }

    private void RefreshMonitors()
    {
        var primary = PrimaryDisplayProbe.Read();
        _monitors.Update(primary is null ? [] : [primary]);
    }

    private void SetState(DesktopShellState state)
    {
        bool changed;
        lock (_lifecycleGate)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    private async Task JoinThreadAsync(Thread thread) =>
        await Task.Run(() =>
        {
            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                _logger.LogWarning("The desktop shell thread did not stop in time");
            }
        }).ConfigureAwait(false);
}
