using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The single owner of the desktop layer's windowing: it creates the popup window a surface lives
/// in, attaches it to the wallpaper worker, keeps it positioned over its display and destroys it
/// again. It knows nothing about what a surface shows — video, and future canvas or widget content,
/// all get the same correctly placed window.
/// </summary>
/// <remarks>
/// Every method runs on the shell thread, which also dispatches the window procedure below; nothing
/// here is thread-safe on its own because nothing calls it from anywhere else.
/// </remarks>
internal sealed class Win32SurfaceHost : ISurfaceHost
{
    private const string WindowClassName = "MuralisDesktopHostWindow";

    private static readonly object WindowTableGate = new();
    private static readonly Dictionary<nint, DesktopSurface> LiveWindows = [];
    private static readonly NativeMethods.WindowProc WindowCallback = OnWindowMessage;
    private static nint _instance;
    private static bool _classRegistered;

    private readonly ILogger _logger;
    private readonly Action<DesktopSurface> _onWindowLost;

    private nint _worker;

    internal Win32SurfaceHost(ILogger logger, Action<DesktopSurface> onWindowLost)
    {
        _logger = logger;
        _onWindowLost = onWindowLost;
    }

    public Task<IDesktopSurface> CreateAsync(SurfaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IDesktopSurface>(new DesktopSurface(request, this, _onWindowLost));
    }

    /// <summary>
    /// Destroys a surface's window and releases its mount. The surface object stays valid; only the
    /// window is gone, and the content has been asked to unmount.
    /// </summary>
    public Task DestroyAsync(IDesktopSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return ((DesktopSurface)surface).DetachAsync();
    }

    /// <summary>
    /// Creates the window, parents it to the wallpaper worker and places it over
    /// <paramref name="geometry"/>. Throws when any of the three steps fails, leaving no window.
    /// </summary>
    internal nint CreateWindow(nint worker, MonitorGeometry geometry)
    {
        EnsureWindowClass();

        // Created as a popup and re-parented: that is the combination the shell expects from
        // wallpaper hosts, and it keeps the window out of the taskbar and Alt+Tab.
        var window = NativeMethods.CreateWindowExW(
            NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
            WindowClassName,
            "Muralis",
            NativeMethods.WsPopup,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            _instance,
            nint.Zero);

        if (window == nint.Zero)
        {
            throw new InvalidOperationException($"The desktop surface window could not be created (error {Marshal.GetLastWin32Error()}).");
        }

        _worker = worker;
        NativeMethods.SetParent(window, worker);

        try
        {
            PositionWindow(window, geometry.Bounds);
        }
        catch
        {
            NativeMethods.DestroyWindow(window);
            throw;
        }

        _logger.LogDebug("The desktop surface window 0x{Window:X} is attached to the wallpaper worker", window);
        return window;
    }

    /// <summary>Starts routing this window's messages to the surface that lives in it.</summary>
    internal void Track(nint window, DesktopSurface surface)
    {
        lock (WindowTableGate)
        {
            LiveWindows[window] = surface;
        }
    }

    /// <summary>Moves the window so it covers <paramref name="bounds"/> of the virtual desktop.</summary>
    internal void PositionWindow(nint window, PixelRect bounds)
    {
        // A child window is positioned in its parent's client space, which is the whole virtual
        // desktop, so screen coordinates have to be shifted by the worker's origin.
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(_worker, ref origin);

        // HWND_TOP keeps the surface on top of whatever else draws in the wallpaper layer, while the
        // icons stay visible because their window is above the worker in the shell's own z-order.
        if (!NativeMethods.SetWindowPos(
                window,
                nint.Zero,
                bounds.X - origin.X,
                bounds.Y - origin.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new InvalidOperationException($"The desktop surface window could not be placed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Destroys the window and stops routing its messages. Does nothing when it is gone.</summary>
    internal void DestroyWindow(nint window)
    {
        Forget(window);

        if (NativeMethods.IsWindow(window))
        {
            NativeMethods.DestroyWindow(window);
        }
    }

    /// <summary>Stops routing messages from a window that is already gone.</summary>
    internal void Forget(nint window)
    {
        lock (WindowTableGate)
        {
            LiveWindows.Remove(window);
        }
    }

    private static nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        DesktopSurface? surface;
        lock (WindowTableGate)
        {
            LiveWindows.TryGetValue(hwnd, out surface);
        }

        if (surface is not null)
        {
            switch (message)
            {
                case NativeMethods.WmEraseBackground:
                    return 1;

                case NativeMethods.WmDestroy:
                    // Explorer took the wallpaper layer down and the window died with it. The shell
                    // re-mounts the surface on the desktop layer it rebuilds.
                    lock (WindowTableGate)
                    {
                        LiveWindows.Remove(hwnd);
                    }

                    surface.OnWindowDestroyed();
                    return nint.Zero;
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnsureWindowClass()
    {
        lock (WindowTableGate)
        {
            if (_classRegistered)
            {
                return;
            }

            _instance = NativeMethods.GetModuleHandleW(null);
            var windowClass = new NativeMethods.WindowClass
            {
                WndProc = Marshal.GetFunctionPointerForDelegate(WindowCallback),
                Instance = _instance,
                Background = nint.Zero,
                Cursor = nint.Zero,
                ClassName = WindowClassName,
            };

            if (NativeMethods.RegisterClassW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException($"The desktop surface window class could not be registered (error {Marshal.GetLastWin32Error()}).");
            }

            _classRegistered = true;
        }
    }
}
