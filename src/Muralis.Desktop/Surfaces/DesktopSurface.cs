using Muralis.Core.Models;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// One registered mount: content bound to a display and the window it currently shows in. The shell
/// drives it — it hands over a target to mount on, tells it when the display changed and releases it
/// when the mount is no longer wanted. The surface keeps the two facts the shell cannot derive: the
/// content, and whether the last mount is still intact.
/// </summary>
internal sealed class DesktopSurface : IDesktopSurface
{
    private readonly SurfaceRequest _request;
    private readonly Win32SurfaceHost _host;
    private readonly Action<DesktopSurface> _onWindowLost;

    private nint _window;
    private double _scaleFactor = 1.0;

    internal DesktopSurface(SurfaceRequest request, Win32SurfaceHost host, Action<DesktopSurface> onWindowLost)
    {
        _request = request;
        _host = host;
        _onWindowLost = onWindowLost;
        Monitor = request.Monitor;
    }

    public DesktopSurfaceId Id { get; } = DesktopSurfaceId.New();

    public ISurfaceContent Content => _request.Content;

    public SurfaceState State { get; private set; } = SurfaceState.Detached;

    public MonitorRef Monitor { get; private set; }

    /// <summary>Position within the display's surface stack, higher on top. One backdrop per display for now.</summary>
    public int ZOrder => 0;

    internal nint WindowHandle => _window;

    internal PixelRect Bounds { get; private set; }

    internal bool IsWindowAlive => _window != nint.Zero && NativeMethods.IsWindow(_window);

    /// <summary>Re-mount attempts since the mount was lost; reset after a successful mount.</summary>
    internal int RemountAttempts { get; set; }

    public async Task AttachAsync(ISurfaceTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is not IWin32SurfaceTarget win32)
        {
            throw new ArgumentException(
                $"The desktop surface host hands out Win32 targets, not {target.GetType().Name}.",
                nameof(target));
        }

        _window = win32.WindowHandle;
        _scaleFactor = target.ScaleFactor;
        Bounds = target.PixelBounds;

        try
        {
            await Content.MountAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The content did not make it onto the window: release whatever it acquired and forget
            // the window. The caller destroys it and can retry with a fresh one.
            _window = nint.Zero;
            await Content.UnmountAsync().ConfigureAwait(false);
            throw;
        }

        State = SurfaceState.Mounted;
        RemountAttempts = 0;
    }

    public Task DetachAsync()
    {
        Detach();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases the mount but keeps the surface and its content registered: used when the window is
    /// taken away without the intent to show ending.
    /// </summary>
    internal void Detach()
    {
        if (_window != nint.Zero)
        {
            _host.DestroyWindow(_window);
            _window = nint.Zero;
        }

        Content.UnmountAsync().GetAwaiter().GetResult();
        State = SurfaceState.Detached;
    }

    /// <summary>
    /// The window died under the surface (Explorer restart, worker destroyed): the mount is gone but
    /// the intent to show is not, so the shell re-mounts this surface on the new desktop layer.
    /// </summary>
    internal void OnWindowDestroyed()
    {
        if (!MarkOrphaned())
        {
            return;
        }

        _onWindowLost(this);
    }

    /// <summary>
    /// Unmounts the content and marks the surface orphaned. Returns false when there was no live
    /// mount to lose — a destroy after a deliberate detach, or a second notification for the same
    /// loss, which both the window procedure and the shell's safety net can notice.
    /// </summary>
    internal bool MarkOrphaned()
    {
        if (State != SurfaceState.Mounted)
        {
            return false;
        }

        var window = _window;
        _window = nint.Zero;
        if (window != nint.Zero)
        {
            _host.Forget(window);
        }

        Content.UnmountAsync().GetAwaiter().GetResult();
        State = SurfaceState.Orphaned;
        return true;
    }

    public void MoveTo(MonitorRef monitor, MonitorGeometry geometry)
    {
        Monitor = monitor;
        Bounds = geometry.Bounds;

        if (IsWindowAlive)
        {
            _host.PositionWindow(_window, geometry.Bounds);
        }

        Content.OnGeometryChanged(geometry, _scaleFactor);
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        await Content.DisposeAsync().ConfigureAwait(false);
    }
}
