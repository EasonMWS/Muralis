using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Surfaces.Compatibility;

/// <summary>
/// Plays a video on the desktop behind the icons. Each start registers one backdrop surface on the
/// primary display with the desktop shell; stopping removes it and reveals the static wallpaper
/// again, which is never modified. The video is scaled to fit the display while keeping its aspect
/// ratio, so a clip whose aspect ratio differs from the display is letterboxed.
/// </summary>
/// <remarks>
/// UI compatibility API (Phase 1F): the App and the dynamic wallpaper page still resolve the
/// session-era <see cref="IVideoWallpaperService"/>, and this class keeps that contract on top of
/// <see cref="IDesktopShell"/>. It is a thin adapter — no thread, no window, no WorkerW lookup and
/// no re-mounting live here: the shell owns the desktop layer and <see cref="VideoSurfaceContent"/>
/// renders the video. The whole seam (this class, its registration in <c>AppHost</c> and the
/// interface itself) is deleted once <see cref="IDesktopBackdropService"/> takes over the UI.
/// </remarks>
public sealed class VideoWallpaperServiceAdapter : IVideoWallpaperService
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<VideoWallpaperServiceAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDesktopShell _shell;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private VideoSurfaceContent? _content;
    private IDesktopSurface? _surface;
    private VideoWallpaperStatus _status = VideoWallpaperStatus.Stopped;

    public VideoWallpaperServiceAdapter(ILoggerFactory loggerFactory, IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(shell);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VideoWallpaperServiceAdapter>();
        _shell = shell;
    }

    public VideoWallpaperStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// How many video frames have reached the desktop since the current video was mounted. Diagnostic
    /// hook used by the standalone host verification to prove frames are flowing without screen capture.
    /// </summary>
    public long PresentedFrames => Volatile.Read(ref _content)?.PresentedFrames ?? 0;

    public event EventHandler<VideoWallpaperStatus>? StatusChanged;

    public async Task<VideoWallpaperStatus> StartAsync(
        string videoPath,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var fullPath = Path.GetFullPath(videoPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The video file was not found.", fullPath);
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopVideoAsync().ConfigureAwait(false);

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

            var content = new VideoSurfaceContent(fullPath, muted, _loggerFactory.CreateLogger<VideoSurfaceContent>());
            content.StatusChanged += OnContentStatusChanged;
            _content = content;
            Publish(new VideoWallpaperStatus(VideoWallpaperState.Starting, fullPath));

            VideoWallpaperStatus status;
            try
            {
                _surface = await _shell
                    .AddSurfaceAsync(new SurfaceRequest(content, MonitorRef.From(primary)), cancellationToken)
                    .ConfigureAwait(false);

                status = await content.Started.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopVideoAsync().ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                await StopVideoAsync().ConfigureAwait(false);
                status = new VideoWallpaperStatus(
                    VideoWallpaperState.Failed,
                    fullPath,
                    "The desktop host did not start in time.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The video wallpaper could not be shown");
                await StopVideoAsync().ConfigureAwait(false);
                status = new VideoWallpaperStatus(VideoWallpaperState.Failed, fullPath, ex.Message);
            }

            Publish(status);

            if (status.State == VideoWallpaperState.Failed)
            {
                // No half-mounted surface is left behind: when the file, its codec or the mount is
                // unusable the desktop falls back to the static wallpaper instead of an empty window.
                await StopVideoAsync().ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wasRunning = _content is not null;
            await StopVideoAsync().ConfigureAwait(false);
            if (wasRunning)
            {
                _logger.LogInformation("Video wallpaper stopped");
            }

            Publish(VideoWallpaperStatus.Stopped);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Removes the video from the shell. Safe to call when nothing is running.</summary>
    private async Task StopVideoAsync()
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

    private void Publish(VideoWallpaperStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
