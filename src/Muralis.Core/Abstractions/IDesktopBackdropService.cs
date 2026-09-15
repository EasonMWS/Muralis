using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Puts desktop background content (currently video) on a specific display. Implemented by
/// <c>Muralis.Desktop</c> so view models never touch native APIs. It is monitor-aware and driven
/// by <see cref="BackdropSpec"/>; it never reads profiles or scenes itself — asset references
/// arrive resolved by the caller. This is the eventual replacement for
/// <see cref="IVideoWallpaperService"/>, which stays as the compatibility adapter until the
/// scene orchestration (Phase 4) lands.
/// </summary>
public interface IDesktopBackdropService
{
    /// <summary>
    /// Applies <paramref name="spec"/> to one display, replacing whatever is on it. The static
    /// desktop background stays untouched underneath.
    /// </summary>
    Task<BackdropStatus> ApplyAsync(MonitorRef monitor, BackdropSpec spec, CancellationToken cancellationToken);

    /// <summary>Removes the backdrop from one display and reveals the static background again.</summary>
    Task ClearAsync(MonitorRef monitor, CancellationToken cancellationToken);

    /// <summary>Raised whenever any display's backdrop changes. May be raised on a background thread.</summary>
    event EventHandler<BackdropStatusChanged>? StatusChanged;
}
