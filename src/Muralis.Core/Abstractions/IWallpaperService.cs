using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Applies wallpapers to the desktop. Implemented per platform in the app layer
/// (<c>Muralis.App/Services/Platform</c>) so view models never touch native APIs.
/// </summary>
public interface IWallpaperService
{
    /// <summary>Connected displays, primary first.</summary>
    Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the desktop background. When <paramref name="monitorId"/> is null the image is applied
    /// to every display; otherwise only to the matching monitor.
    /// Returns the path of the file that was actually applied, which can differ from the input
    /// when the image had to be transcoded into a format Windows supports.
    /// </summary>
    Task<string> SetWallpaperAsync(
        string imagePath,
        WallpaperFitMode fitMode,
        string? monitorId = null,
        CancellationToken cancellationToken = default);
}
