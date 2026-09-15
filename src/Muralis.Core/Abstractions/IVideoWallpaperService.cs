using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Plays a video file as an animated desktop background, behind the desktop icons. Implemented per
/// platform in the app layer (<c>Muralis.Desktop</c>) so view models never touch native APIs.
/// </summary>
/// <remarks>
/// UI compatibility API (Phase 1F): this is the session-era contract the dynamic wallpaper page
/// and the start-with-app restore still resolve. Its implementation is a thin adapter over the
/// desktop shell, and the whole seam is deleted once <see cref="IDesktopBackdropService"/> takes
/// over the UI.
/// </remarks>
public interface IVideoWallpaperService
{
    /// <summary>What the video wallpaper is doing right now.</summary>
    VideoWallpaperStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes. May be raised on a background thread.</summary>
    event EventHandler<VideoWallpaperStatus>? StatusChanged;

    /// <summary>
    /// Puts <paramref name="videoPath"/> on the desktop, replacing whatever is playing. The video is
    /// scaled to fit the display while keeping its aspect ratio; the rest of the screen stays black.
    /// The static desktop background is left untouched underneath.
    /// </summary>
    Task<VideoWallpaperStatus> StartAsync(
        string videoPath,
        bool muted,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the video from the desktop and reveals the static background again.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
