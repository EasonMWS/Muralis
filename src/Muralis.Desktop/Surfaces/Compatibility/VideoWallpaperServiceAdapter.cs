using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;

namespace Muralis.Desktop.Surfaces.Compatibility;

/// <summary>
/// Plays a video on the desktop behind the icons. Each start registers one backdrop surface on the
/// primary display with the desktop shell; stopping removes it and reveals the static wallpaper
/// again, which is never modified. The video is scaled to fit the display while keeping its aspect
/// ratio, so a clip whose aspect ratio differs from the display is letterboxed.
/// </summary>
/// <remarks>
/// Temporary compatibility adapter (Phase 1E): the App and the dynamic wallpaper page still
/// resolve the session-era <see cref="IVideoWallpaperService"/>, and this class keeps that
/// contract on top of <see cref="IDesktopShell"/>. It is a pure facade — no thread, no window, no
/// WorkerW lookup and no re-mounting live here — so the shell stays the only owner of the desktop
/// layer and <see cref="VideoSurfaceContent"/> stays the only place that renders video.
/// <para>
/// Removal (Phase 1F): the App moves start/stop/status to the shell-facing backdrop service (the
/// <see cref="Muralis.Core.Abstractions.IDesktopBackdropService"/> draft), then this class, its
/// registration in <c>AppHost</c>, <c>DesktopHostSession</c> and the no-op
/// <see cref="NotifyShellRestarted"/> are deleted together with the
/// <see cref="IVideoWallpaperService"/> seam.
/// </para>
/// </remarks>
public sealed class VideoWallpaperServiceAdapter : IVideoWallpaperService
{
    private readonly ILogger<VideoWallpaperServiceAdapter> _logger;
    private readonly IDesktopShell _shell;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private DesktopHostSession? _session;
    private VideoWallpaperStatus _status = VideoWallpaperStatus.Stopped;

    public VideoWallpaperServiceAdapter(ILogger<VideoWallpaperServiceAdapter> logger, IDesktopShell shell)
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

            var session = new DesktopHostSession(_shell, _logger);
            session.StatusChanged += OnSessionStatusChanged;
            _session = session;

            var status = await session.StartAsync(fullPath, muted, cancellationToken).ConfigureAwait(false);
            Publish(status);

            if (status.State == VideoWallpaperState.Failed)
            {
                // The session released its surface when the start failed; drop the dead reference
                // so stopping later does not report work that is not there.
                await StopSessionAsync().ConfigureAwait(false);
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
        if (session is null)
        {
            return;
        }

        _session = null;
        session.StatusChanged -= OnSessionStatusChanged;

        // Ends the session: the shell removes the mount and releases the media player and the
        // swap chain behind it.
        await session.StopAsync().ConfigureAwait(false);
    }

    private void OnSessionStatusChanged(object? sender, VideoWallpaperStatus status) => Publish(status);

    private void Publish(VideoWallpaperStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
