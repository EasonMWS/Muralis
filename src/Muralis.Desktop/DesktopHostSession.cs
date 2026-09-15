using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop;

/// <summary>
/// One video backdrop session on the desktop shell: it creates the video content, registers it as
/// a single surface on the primary display, waits for the first frame and removes the surface
/// again when the session ends. It owns no thread, window, worker lookup or rendering — the shell
/// hosts the surface and <see cref="VideoSurfaceContent"/> renders the video.
/// </summary>
/// <remarks>
/// Temporary compatibility glue (Phase 1E). The session-era service still needs a place that ties
/// one start request to one surface and one status stream while the App keeps resolving
/// <c>IVideoWallpaperService</c>. Removal (Phase 1F): the App moves to the shell-facing backdrop
/// service, then this class, the <c>VideoWallpaperServiceAdapter</c> and the
/// <c>IVideoWallpaperService</c> seam are deleted together. Until then keep this class free of
/// video and native code — anything that renders belongs in <see cref="VideoSurfaceContent"/>, and
/// anything that creates or re-mounts windows belongs in the shell.
/// </remarks>
internal sealed class DesktopHostSession
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger;
    private readonly IDesktopShell _shell;
    private VideoSurfaceContent? _content;
    private IDesktopSurface? _surface;

    internal DesktopHostSession(IDesktopShell shell, ILogger logger)
    {
        _shell = shell;
        _logger = logger;
    }

    /// <summary>How many frames the current video has put on the desktop; 0 when nothing is running.</summary>
    internal long PresentedFrames => Volatile.Read(ref _content)?.PresentedFrames ?? 0;

    /// <summary>Raised whenever the session's status changes. Fires on the shell, player or pool thread.</summary>
    internal event EventHandler<VideoWallpaperStatus>? StatusChanged;

    /// <summary>
    /// Puts the video on the primary display and waits for its first frame. The session cleans
    /// itself up when the start fails, so no half-mounted surface is left behind.
    /// </summary>
    internal async Task<VideoWallpaperStatus> StartAsync(string fullPath, bool muted, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting video wallpaper {Path} (muted: {Muted})", fullPath, muted);

        var primary = _shell.Monitors.Primary;
        if (primary is null)
        {
            var missing = new VideoWallpaperStatus(
                VideoWallpaperState.Failed,
                fullPath,
                "No display was found to place the video wallpaper on.");
            Publish(missing);
            return missing;
        }

        var content = new VideoSurfaceContent(fullPath, muted, _logger);
        content.StatusChanged += OnContentStatusChanged;
        _content = content;
        Publish(new VideoWallpaperStatus(VideoWallpaperState.Starting, fullPath));

        try
        {
            _surface = await _shell
                .AddSurfaceAsync(new SurfaceRequest(content, MonitorRef.From(primary)), cancellationToken)
                .ConfigureAwait(false);

            var status = await content.Started.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            Publish(status);

            if (status.State == VideoWallpaperState.Failed)
            {
                // The file or its codec is unusable: the desktop must fall back to the static
                // wallpaper instead of showing an empty window.
                await StopAsync().ConfigureAwait(false);
            }

            return status;
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await StopAsync().ConfigureAwait(false);
            var timedOut = new VideoWallpaperStatus(
                VideoWallpaperState.Failed,
                fullPath,
                "The desktop host did not start in time.");
            Publish(timedOut);
            return timedOut;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The video wallpaper could not be shown");
            await StopAsync().ConfigureAwait(false);
            var failed = new VideoWallpaperStatus(VideoWallpaperState.Failed, fullPath, ex.Message);
            Publish(failed);
            return failed;
        }
    }

    /// <summary>Removes the video from the shell. Safe to call when nothing is running.</summary>
    internal async Task StopAsync()
    {
        var content = _content;
        var surface = _surface;
        if (content is null)
        {
            return;
        }

        _content = null;
        _surface = null;
        content.StatusChanged -= OnContentStatusChanged;

        if (surface is not null)
        {
            // Removes the mount and releases the media player and the swap chain behind it.
            await _shell.RemoveSurfaceAsync(surface, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnContentStatusChanged(object? sender, VideoWallpaperStatus status) => Publish(status);

    private void Publish(VideoWallpaperStatus status) => StatusChanged?.Invoke(this, status);
}
