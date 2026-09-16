namespace Muralis.Desktop.Interop;

/// <summary>
/// Finds the two desktop layer windows surfaces are placed in: the window the wallpaper is drawn
/// in (the layer directly below the desktop icons) and the window the icons themselves live under.
/// The shell arranges the wallpaper layer in one of two ways - the worker is a top level window
/// below the icon host (older builds), or a child of the icon host below the icon view
/// (Windows 11, the same layer desktop wallpaper apps draw in). Asking the shell to prepare one is
/// only needed when neither exists; the request is ignored while a worker is already there.
/// </summary>
internal static class DesktopWorkerWindow
{
    private const string IconViewClass = "SHELLDLL_DefView";
    private const string WorkerClass = "WorkerW";
    private const string ProgmanClass = "Progman";

    private static readonly NativeMethods.EnumWindowsProc FindCallback = OnEnumWindow;
    private static readonly NativeMethods.EnumWindowsProc FindIconHostCallback = OnEnumIconHost;
    private static nint _found;
    private static nint _foundIconHost;

    /// <summary>
    /// Whether a window class belongs to the shell's desktop layer. The pointer classifier asks
    /// this instead of carrying the class names itself, so the names of the desktop layer windows
    /// are known in exactly one place.
    /// </summary>
    internal static bool IsDesktopLayerClass(string? className) =>
        string.Equals(className, IconViewClass, StringComparison.Ordinal)
        || string.Equals(className, WorkerClass, StringComparison.Ordinal)
        || string.Equals(className, ProgmanClass, StringComparison.Ordinal);

    /// <summary>
    /// The window that directly parents the desktop icons. Interactive surfaces become its
    /// topmost children, which is what puts them above the icons without touching the icon view.
    /// </summary>
    internal static nint FindIconHost()
    {
        _foundIconHost = nint.Zero;
        NativeMethods.EnumWindows(FindIconHostCallback, nint.Zero);
        return _foundIconHost;
    }

    /// <summary>Returns the wallpaper worker, asking the shell to create one only if there is none.</summary>
    internal static nint FindOrCreate()
    {
        var existing = Find();
        if (existing != nint.Zero)
        {
            return existing;
        }

        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman != nint.Zero)
        {
            // Undocumented, but stable since Windows 7: makes the shell split the desktop into a
            // worker that hosts the icons and one that can be used as a wallpaper surface.
            NativeMethods.SendMessageTimeoutW(
                progman,
                NativeMethods.SpawnWallpaperWorker,
                nint.Zero,
                nint.Zero,
                NativeMethods.SmtoNormal,
                1000,
                out _);
        }

        return Find();
    }

    private static nint Find()
    {
        _found = nint.Zero;
        NativeMethods.EnumWindows(FindCallback, nint.Zero);
        return _found;
    }

    private static bool OnEnumIconHost(nint window, nint lParam)
    {
        if (NativeMethods.FindWindowExW(window, nint.Zero, IconViewClass, null) != nint.Zero)
        {
            _foundIconHost = window;
            return false;
        }

        // The icon view can sit one level deeper: on some builds the shell moves it under a
        // worker of its own, and then that worker is the window the icons live under.
        for (var child = NativeMethods.GetWindow(window, NativeMethods.GwChild);
             child != nint.Zero;
             child = NativeMethods.GetWindow(child, NativeMethods.GwHwndNext))
        {
            if (NativeMethods.FindWindowExW(child, nint.Zero, IconViewClass, null) != nint.Zero)
            {
                _foundIconHost = child;
                return false;
            }
        }

        return true;
    }

    private static bool OnEnumWindow(nint window, nint lParam)
    {
        var iconView = NativeMethods.FindWindowExW(window, nint.Zero, IconViewClass, null);
        if (iconView == nint.Zero)
        {
            return true;
        }

        // Older builds: the icon host is a top level window, the worker is the top level window
        // right below it in z-order.
        _found = NativeMethods.FindWindowExW(nint.Zero, window, WorkerClass, null);
        if (_found == nint.Zero)
        {
            // Windows 11: the worker is a child of the icon host, right below the icon view.
            _found = NativeMethods.FindWindowExW(window, iconView, WorkerClass, null);
        }

        return false;
    }
}
