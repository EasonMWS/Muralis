using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.DesktopHost;

/// <summary>
/// Plays a video on the desktop behind the icons. Each start puts a fresh host window on the
/// desktop; stopping removes it and reveals the static wallpaper again, which is never modified.
/// </summary>
/// <remarks>
/// The video is scaled to fit the display while keeping its aspect ratio - what the media player's
/// frame server can do - so a clip whose aspect ratio differs from the display is letterboxed.
/// </remarks>
public sealed class DesktopVideoWallpaperService : IVideoWallpaperService
{
    private readonly ILogger<DesktopVideoWallpaperService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private DesktopHostSession? _session;
    private VideoWallpaperStatus _status = VideoWallpaperStatus.Stopped;

    public DesktopVideoWallpaperService(ILogger<DesktopVideoWallpaperService> logger) => _logger = logger;

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
            var session = new DesktopHostSession(fullPath, muted, _logger);
            session.StatusChanged += OnSessionStatusChanged;
            _session = session;
            Publish(new VideoWallpaperStatus(VideoWallpaperState.Starting, fullPath));

            var status = await session.StartAsync(cancellationToken).ConfigureAwait(false);
            Publish(status);

            if (status.State == VideoWallpaperState.Failed)
            {
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

    private async Task StopSessionAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        session.StatusChanged -= OnSessionStatusChanged;
        await session.StopAsync().ConfigureAwait(false);
    }

    private void OnSessionStatusChanged(object? sender, VideoWallpaperStatus status) => Publish(status);

    private void Publish(VideoWallpaperStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
