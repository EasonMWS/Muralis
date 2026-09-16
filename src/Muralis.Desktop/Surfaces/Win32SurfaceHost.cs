using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The single owner of the desktop layer's windowing: it creates the popup window a surface lives
/// in, attaches it to the right desktop window, keeps it positioned over its display and destroys
/// it again. It knows nothing about what a surface shows — video, and future canvas or widget
/// content, all get the same correctly placed window.
/// </summary>
/// <remarks>
/// Every method runs on the shell thread, which also dispatches the window procedure below; nothing
/// here is thread-safe on its own because nothing calls it from anywhere else.
/// </remarks>
internal sealed class Win32SurfaceHost : ISurfaceHost
{
    private const string WindowClassName = "MuralisDesktopHostWindow";

    private static readonly object WindowTableGate = new();
    private static readonly Dictionary<nint, WindowEntry> LiveWindows = [];
    private static readonly NativeMethods.WindowProc WindowCallback = OnWindowMessage;
    private static nint _instance;
    private static bool _classRegistered;

    private readonly ILogger _logger;
    private readonly Action<DesktopSurface> _onWindowLost;

    internal Win32SurfaceHost(ILogger logger, Action<DesktopSurface> onWindowLost)
    {
        _logger = logger;
        _onWindowLost = onWindowLost;
    }

    /// <summary>One live surface window: the window it was parented to, and the surface once it is tracked.</summary>
    private sealed class WindowEntry(nint parent)
    {
        internal nint Parent { get; } = parent;

        internal DesktopSurface? Surface { get; set; }
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
    /// Creates the window, parents it to <paramref name="parent"/> and places it over
    /// <paramref name="geometry"/>. Throws when any of the three steps fails, leaving no window.
    /// </summary>
    internal nint CreateWindow(nint parent, SurfaceKind kind, MonitorGeometry geometry)
    {
        EnsureWindowClass();

        // Created as a popup and re-parented: that is the combination the shell expects from
        // desktop hosts, and it keeps the window out of the taskbar and Alt+Tab.
        var window = NativeMethods.CreateWindowExW(
            ExStyleFor(kind),
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

        lock (WindowTableGate)
        {
            LiveWindows[window] = new WindowEntry(parent);
        }

        try
        {
            NativeMethods.SetParent(window, parent);
            PositionWindow(window, geometry.Bounds);
        }
        catch
        {
            Forget(window);
            NativeMethods.DestroyWindow(window);
            throw;
        }

        _logger.LogDebug("The desktop surface window 0x{Window:X} is attached to 0x{Parent:X}", window, parent);
        return window;
    }

    /// <summary>Starts routing this window's messages to the surface that lives in it.</summary>
    internal void Track(nint window, DesktopSurface surface)
    {
        lock (WindowTableGate)
        {
            if (LiveWindows.TryGetValue(window, out var entry))
            {
                entry.Surface = surface;
            }
        }
    }

    /// <summary>Moves the window so it covers <paramref name="bounds"/> of the virtual desktop.</summary>
    internal void PositionWindow(nint window, PixelRect bounds)
    {
        var parent = ParentOf(window);

        // A child window is positioned in its parent's client space, which is the whole virtual
        // desktop, so screen coordinates have to be shifted by the parent's origin.
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(parent, ref origin);

        // HWND_TOP inserts the window at the top of its parent's children: for a wallpaper host
        // that keeps the surface above whatever else draws in that layer (the icons stay visible
        // because their window is a sibling further up), and for an interactive surface it is what
        // places the window above the icon view.
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

    /// <summary>
    /// Re-inserts a window at the top of its parent's children without touching its geometry.
    /// Explorer can reshuffle the icon host's children; the shell re-asserts the order after it
    /// checked the desktop state.
    /// </summary>
    internal void BringToTop(nint window)
    {
        if (!NativeMethods.IsWindow(window))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            window,
            nint.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
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

    /// <summary>
    /// Window styles per surface kind. Both kinds stay out of the taskbar and Alt+Tab and never
    /// steal focus; interactive content draws through the compositor, which needs a window
    /// without a redirection bitmap.
    /// </summary>
    private static uint ExStyleFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.InteractiveOverlay =>
            NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExNoRedirectionBitmap,
        _ => NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
    };

    private static nint ParentOf(nint window)
    {
        lock (WindowTableGate)
        {
            return LiveWindows.TryGetValue(window, out var entry) ? entry.Parent : nint.Zero;
        }
    }

    private static nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        DesktopSurface? surface;
        lock (WindowTableGate)
        {
            LiveWindows.TryGetValue(hwnd, out var entry);
            surface = entry?.Surface;
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

            // Everything else belongs to the content when it handles its own input; the window
            // itself stays owned here.
            if (surface.Content is ISurfaceMessageSink sink
                && sink.OnWindowMessage(hwnd, message, wParam, lParam, out var handled))
            {
                return handled;
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
