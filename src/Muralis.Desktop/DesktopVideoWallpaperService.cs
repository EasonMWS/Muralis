using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop;

/// <summary>
/// Plays a video on the desktop behind the icons. Each start registers one backdrop surface with
/// the desktop shell; stopping removes it and reveals the static wallpaper again, which is never
/// modified.
/// </summary>
/// <remarks>
/// The video is scaled to fit the display while keeping its aspect ratio - what the media player's
/// frame server can do - so a clip whose aspect ratio differs from the display is letterboxed.
/// <para>
/// Temporary compatibility bridge (Phase 1D): the App and the dynamic wallpaper page still resolve
/// the session-era <see cref="IVideoWallpaperService"/>, and this class keeps that contract on top
/// of <see cref="IDesktopShell"/>. It is a pure adapter - no thread, no window, no WorkerW lookup
/// and no re-mounting live here - so the shell stays the only owner of the desktop layer.
/// </para>
/// <para>
/// Removal (Phase 1E / 1F): the App moves start/stop/status to the shell-facing backdrop service
/// (the <see cref="IDesktopBackdropService"/> draft), then this class, its registration in
/// <c>AppHost</c> and the no-op <see cref="NotifyShellRestarted"/> are deleted together with the
/// <see cref="IVideoWallpaperService"/> seam. Until then keep this class free of desktop-layer
/// logic: anything that creates, finds or re-mounts windows belongs in <see cref="DesktopShell"/>.
/// </para>
/// </remarks>
public sealed class DesktopVideoWallpaperService : IVideoWallpaperService
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<DesktopVideoWallpaperService> _logger;
    private readonly IDesktopShell _shell;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private DesktopHostSession? _session;
    private IDesktopSurface? _surface;
    private VideoWallpaperStatus _status = VideoWallpaperStatus.Stopped;

    public DesktopVideoWallpaperService(ILogger<DesktopVideoWallpaperService> logger, IDesktopShell shell)
    {
        _logger = logger;
        _shell = shell;
    }

    public VideoWallpaperStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// How many video frames have reached the desktop in the current session. Diagnostic hook used
    /// by the standalone host verification to prove frames are flowing without screen capture.
    /// </summary>
    public long PresentedFrames => Volatile.Read(ref _session)?.PresentedFrames ?? 0;

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
            await StopSessionAsync().ConfigureAwait(false);

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

            var session = new DesktopHostSession(fullPath, muted, _logger);
            session.StatusChanged += OnSessionStatusChanged;
            _session = session;
            Publish(new VideoWallpaperStatus(VideoWallpaperState.Starting, fullPath));

            try
            {
                _surface = await _shell
                    .AddSurfaceAsync(new SurfaceRequest(session, MonitorRef.From(primary)), cancellationToken)
                    .ConfigureAwait(false);

                var status = await session.Started.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
                Publish(status);

                if (status.State == VideoWallpaperState.Failed)
                {
                    // The file or its codec is unusable: the desktop must fall back to the static
                    // wallpaper instead of showing an empty window.
                    await StopSessionAsync().ConfigureAwait(false);
                }

                return status;
            }
            catch (OperationCanceledException)
            {
                await StopSessionAsync().ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                await StopSessionAsync().ConfigureAwait(false);
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
                await StopSessionAsync().ConfigureAwait(false);
                var failed = new VideoWallpaperStatus(VideoWallpaperState.Failed, fullPath, ex.Message);
                Publish(failed);
                return failed;
            }
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
            var wasRunning = _session is not null;
            await StopSessionAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Kept for the session-era interface. Explorer-restart recovery now belongs to the desktop
    /// shell, which watches the shell events and re-mounts the surfaces on its own, so there is
    /// nothing left to forward.
    /// </summary>
    public void NotifyShellRestarted() =>
        _logger.LogInformation("Explorer restarted; the desktop shell re-mounts desktop content on its own");

    private async Task StopSessionAsync()
    {
        var session = _session;
        var surface = _surface;
        if (session is null)
        {
            return;
        }

        _session = null;
        _surface = null;
        session.StatusChanged -= OnSessionStatusChanged;

        if (surface is not null)
        {
            // Removes the mount and releases the media player and the swap chain behind it.
            await _shell.RemoveSurfaceAsync(surface, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnSessionStatusChanged(object? sender, VideoWallpaperStatus status) => Publish(status);

    private void Publish(VideoWallpaperStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
