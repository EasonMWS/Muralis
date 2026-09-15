using Microsoft.Extensions.Logging;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Shell;

/// <summary>
/// Owns the desktop layer itself: finding the window the wallpaper is drawn in, telling whether a
/// worker that was found is still alive, and re-finding both after Explorer rebuilt the desktop.
/// Surfaces never look for the worker themselves — the shell asks here and hands the result to the
/// surface host, so exactly one place knows how the layer is discovered.
/// </summary>
internal sealed class DesktopLayerHost
{
    private readonly ILogger _logger;

    internal DesktopLayerHost(ILogger logger) => _logger = logger;

    /// <summary>
    /// The current wallpaper worker, or <c>nint.Zero</c> while Explorer has not rebuilt the
    /// desktop layer yet. Asking the shell to create one is part of the lookup: the desktop is
    /// split into an icon worker and a wallpaper worker on request.
    /// </summary>
    internal nint EnsureWorker()
    {
        var worker = DesktopWorkerWindow.FindOrCreate();
        if (worker == nint.Zero)
        {
            return nint.Zero;
        }

        // The worker can be taken down between the enumeration and this line; treating it as gone
        // is what the caller expects from a lookup.
        if (!IsAlive(worker))
        {
            _logger.LogDebug("The desktop worker window disappeared right after it was found");
            return nint.Zero;
        }

        return worker;
    }

    internal bool IsAlive(nint worker) => worker != nint.Zero && NativeMethods.IsWindow(worker);
}
