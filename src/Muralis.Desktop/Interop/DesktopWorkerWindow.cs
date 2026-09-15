namespace Muralis.Desktop.Interop;

/// <summary>
/// Finds the desktop window the wallpaper is drawn in: the layer directly below the desktop icons.
/// The shell arranges that layer in one of two ways - the worker is a top level window below the
/// icon host (older builds), or a child of the icon host below the icon view (Windows 11, the same
/// layer desktop wallpaper apps draw in). Asking the shell to prepare one is only needed when
/// neither exists; the request is ignored while a worker is already there.
/// </summary>
internal static class DesktopWorkerWindow
{
    private const string IconViewClass = "SHELLDLL_DefView";
    private const string WorkerClass = "WorkerW";

    private static readonly NativeMethods.EnumWindowsProc FindCallback = OnEnumWindow;
    private static nint _found;

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
