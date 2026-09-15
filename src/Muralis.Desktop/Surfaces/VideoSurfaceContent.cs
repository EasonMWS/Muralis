using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// The video a backdrop surface shows: a media player in frame server mode copying its frames into
/// a swap chain bound to the window the shell mounted it in. It owns everything video — playback,
/// the rendering resources, the first frame and playback failures — and nothing desktop: the
/// shell owns the thread, the window, the wallpaper worker, positioning and the re-mounting, and
/// hands the window over as a surface target on every mount.
/// </summary>
/// <remarks>
/// Threads: frames arrive on the media player's thread and are presented there; the shell calls
/// mount, unmount and geometry changes from its own thread. <c>_renderGate</c> serializes the two,
/// and the one presentation rebuild below runs on the pool for the same reason. The UI thread
/// never presents a frame.
/// <para>
/// Resource ownership: the swap chain, the D3D device/context and the media player are this
/// content's rendering resources; they are recreated whenever the target or the geometry changes
/// and after a lost device. The window they draw into belongs to <see cref="Win32SurfaceHost"/>
/// and is never created, positioned or destroyed here — a mount hands over a fresh target, an
/// unmount releases the resources, and the content never learns why (Explorer restart, display
/// change, user stop): it only sees mounts come and go.
/// </para>
/// </remarks>
internal sealed class VideoSurfaceContent : ISurfaceContent
{
    /// <summary>DXGI_ERROR_DEVICE_REMOVED / DEVICE_RESET: the device behind the swap chain died.</summary>
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);

    private readonly string _videoPath;
    private readonly bool _muted;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<VideoWallpaperStatus> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serializes frame presentation with every mutation of the rendering resources.</summary>
    private readonly object _renderGate = new();

    private nint _window;
    private PixelRect _displayBounds;
    private D3D11Interop.IDXGISwapChainInterop? _swapChain;
    private IDirect3DSurface? _surface;
    private MediaPlayer? _player;
    private nint _device;
    private nint _deviceContext;
    private int _presentFailures;
    private int _frameErrors;
    private long _presentedFrames;
    private bool _playingReported;
    private bool _everMounted;
    private bool _playbackDisabled;

    // Presentation recovery, guarded by _renderGate: after the device is lost the content rebuilds
    // its resources once per mount generation; a second loss in the same generation gives up.
    private int _presentationEpoch;
    private bool _recoveryAttempted;
    private bool _recoveryScheduled;

    internal VideoSurfaceContent(string videoPath, bool muted, ILogger logger)
    {
        _videoPath = videoPath;
        _muted = muted;
        _logger = logger;
    }

    /// <summary>How many frames have been put on the desktop since this content was created.</summary>
    internal long PresentedFrames => Interlocked.Read(ref _presentedFrames);

    /// <summary>Raised when playback starts, fails or stops. Fires on the shell, player or pool thread.</summary>
    internal event EventHandler<VideoWallpaperStatus>? StatusChanged;

    /// <summary>Completes with the first frame on the desktop, or with the failure that stopped it.</summary>
    internal Task<VideoWallpaperStatus> Started => _started.Task;

    public SurfaceKind Kind => SurfaceKind.Backdrop;

    public SurfaceInteraction Interaction => SurfaceInteraction.None;

    public SurfaceActivation Activation => SurfaceActivation.Never;

    public Task MountAsync(ISurfaceTarget target, CancellationToken cancellationToken)
    {
        if (target is not IWin32SurfaceTarget win32)
        {
            throw new ArgumentException($"The video backdrop needs a Win32 surface target, not {target.GetType().Name}.", nameof(target));
        }

        bool reportStarting;
        lock (_renderGate)
        {
            // A fresh mount, possibly after a lost one: release whatever a previous mount left
            // behind before touching the new window.
            UnmountCore();

            _window = win32.WindowHandle;
            _displayBounds = target.PixelBounds;

            // Fresh resources: the presentation recovery budget starts over.
            _presentationEpoch++;
            _recoveryAttempted = false;
            _recoveryScheduled = false;

            CreatePresentation();

            // The page shows "starting" again while the re-mounted video catches up.
            reportStarting = _everMounted && !_playbackDisabled;
            _everMounted = true;

            if (!_playbackDisabled)
            {
                // The new player starts from scratch: its first frame reports Playing again, which
                // is what tells the page and the logs that a re-mounted video is back on the desktop.
                _playingReported = false;
                StartPlayback();
            }
        }

        if (reportStarting)
        {
            Report(VideoWallpaperState.Starting);
        }

        return Task.CompletedTask;
    }

    public Task UnmountAsync()
    {
        lock (_renderGate)
        {
            UnmountCore();
        }

        return Task.CompletedTask;
    }

    public void OnGeometryChanged(MonitorGeometry geometry, double scale)
    {
        lock (_renderGate)
        {
            if (_window == nint.Zero)
            {
                return;
            }

            _logger.LogInformation(
                "The display changed to {Width}x{Height}; restarting the video wallpaper",
                geometry.Bounds.Width,
                geometry.Bounds.Height);

            StopPlaybackCore();
            _playingReported = false;
            _displayBounds = geometry.Bounds;

            _presentationEpoch++;
            _recoveryAttempted = false;
            _recoveryScheduled = false;

            CreatePresentation();

            if (!_playbackDisabled)
            {
                StartPlayback();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        UnmountAsync().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    /// <summary>Releases the mount. Called under <see cref="_renderGate"/>.</summary>
    private void UnmountCore()
    {
        StopPlaybackCore();
        _window = nint.Zero;
    }

    private void CreatePresentation()
    {
        var description = new D3D11Interop.SwapChainDescription
        {
            BufferDescription = new D3D11Interop.ModeDescription
            {
                Width = (uint)_displayBounds.Width,
                Height = (uint)_displayBounds.Height,
                Format = D3D11Interop.FormatB8G8R8A8Unorm,
            },
            SampleCount = 1,
            BufferUsage = D3D11Interop.UsageRenderTargetOutput,
            BufferCount = 2,
            OutputWindow = _window,
            Windowed = 1,
            SwapEffect = D3D11Interop.SwapEffectDiscard,
        };

        var result = D3D11Interop.CreateDeviceAndSwapChain(
            nint.Zero,
            D3D11Interop.DriverTypeHardware,
            nint.Zero,
            D3D11Interop.CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11Interop.SdkVersion,
            ref description,
            out var swapChain,
            out var device,
            out _,
            out var deviceContext);

        if (result < 0)
        {
            throw new InvalidOperationException($"The desktop swap chain could not be created (0x{result:X8}).");
        }

        // Our reference is the only one holding the device and its context once the swap chain
        // exists, so they are released together with the rest of the presentation.
        _device = device;
        _deviceContext = deviceContext;

        _swapChain = (D3D11Interop.IDXGISwapChainInterop)Marshal.GetObjectForIUnknown(swapChain);
        Marshal.Release(swapChain);

        var surfaceId = D3D11Interop.DxgiSurfaceId;
        _swapChain.GetBuffer(0, ref surfaceId, out var dxgiSurface);
        try
        {
            result = D3D11Interop.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out var graphicsSurface);
            if (result < 0)
            {
                throw new InvalidOperationException($"The desktop surface could not be created (0x{result:X8}).");
            }

            _surface = MarshalInterface<IDirect3DSurface>.FromAbi(graphicsSurface);
            Marshal.Release(graphicsSurface);
        }
        finally
        {
            Marshal.Release(dxgiSurface);
        }
    }

    private void StartPlayback()
    {
        var player = new MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            IsLoopingEnabled = true,
            IsMuted = _muted,
        };

        // No media keys, no transport controls: this is a wallpaper, not a media session.
        player.CommandManager.IsEnabled = false;
        player.SetSurfaceSize(new Windows.Foundation.Size(_displayBounds.Width, _displayBounds.Height));
        player.VideoFrameAvailable += OnVideoFrameAvailable;
        player.MediaFailed += OnMediaFailed;

        _player = player;
        player.Source = MediaSource.CreateFromUri(new Uri(_videoPath));
        player.Play();
    }

    /// <summary>Releases the player and the rendering resources. Called under <see cref="_renderGate"/>.</summary>
    private void StopPlaybackCore()
    {
        var player = _player;
        if (player is not null)
        {
            player.VideoFrameAvailable -= OnVideoFrameAvailable;
            player.MediaFailed -= OnMediaFailed;
            player.Source = null;
            player.Dispose();
            _player = null;
        }

        _surface = null;

        var swapChain = _swapChain;
        _swapChain = null;
        if (swapChain is not null && Marshal.IsComObject(swapChain))
        {
            Marshal.ReleaseComObject(swapChain);
        }

        if (_deviceContext != nint.Zero)
        {
            Marshal.Release(_deviceContext);
            _deviceContext = nint.Zero;
        }

        if (_device != nint.Zero)
        {
            Marshal.Release(_device);
            _device = nint.Zero;
        }
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        try
        {
            lock (_renderGate)
            {
                // A callback from a player that was already replaced carries frames for a
                // presentation that no longer exists.
                if (!ReferenceEquals(sender, _player) || _surface is null || _swapChain is null)
                {
                    return;
                }

                sender.CopyFrameToVideoSurface(_surface);
                var result = _swapChain.Present(1, 0);
                if (result < 0)
                {
                    OnPresentFailed(result);
                    return;
                }

                Interlocked.Increment(ref _presentedFrames);
            }

            if (!_playingReported)
            {
                _playingReported = true;
                _logger.LogInformation("The video wallpaper is playing on the desktop");
                Report(VideoWallpaperState.Playing);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _frameErrors) == 1)
            {
                _logger.LogError(ex, "A video frame could not be put on the desktop");
                Report(VideoWallpaperState.Failed, ex.Message);
            }
        }
    }

    private void OnPresentFailed(int result)
    {
        if (result is DeviceRemoved or DeviceReset)
        {
            OnPresentDeviceLost(result);
            return;
        }

        if (Interlocked.Increment(ref _presentFailures) == 1)
        {
            _logger.LogWarning("Presenting a video frame failed (0x{Result:X8})", result);
        }
    }

    /// <summary>
    /// The device behind the swap chain is gone. One rebuild is attempted per mount generation;
    /// it runs on the pool because the player must not be disposed from inside its own frame
    /// callback, and because the rebuild must not race the shell's mount and geometry calls.
    /// </summary>
    private void OnPresentDeviceLost(int result)
    {
        if (_recoveryScheduled)
        {
            return;
        }

        _recoveryScheduled = true;
        var epoch = _presentationEpoch;

        if (!_recoveryAttempted)
        {
            _recoveryAttempted = true;
            _logger.LogWarning("The desktop swap chain was lost (0x{Result:X8}); rebuilding the video presentation", result);
            _ = Task.Run(() => RebuildPresentation(epoch));
            return;
        }

        _logger.LogError("The desktop swap chain was lost again (0x{Result:X8}); the video wallpaper is stopping", result);
        _ = Task.Run(() => StopAfterPresentationLoss(epoch));
    }

    private void RebuildPresentation(int epoch)
    {
        try
        {
            bool rebuilt;
            lock (_renderGate)
            {
                if (epoch != _presentationEpoch)
                {
                    // A mount or geometry change superseded the rebuild; its fresh resources win.
                    return;
                }

                _recoveryScheduled = false;
                StopPlaybackCore();

                try
                {
                    CreatePresentation();
                    if (!_playbackDisabled)
                    {
                        _playingReported = false;
                        StartPlayback();
                    }

                    rebuilt = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The video presentation could not be rebuilt after the device was lost");
                    StopPlaybackCore();
                    rebuilt = false;
                }
            }

            if (rebuilt)
            {
                _logger.LogInformation("The video presentation was rebuilt after the device was lost");
            }
            else
            {
                Report(VideoWallpaperState.Failed, "The graphics device was lost.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebuilding the video presentation failed");
            Report(VideoWallpaperState.Failed, "The graphics device was lost.");
        }
    }

    private void StopAfterPresentationLoss(int epoch)
    {
        try
        {
            lock (_renderGate)
            {
                if (epoch != _presentationEpoch)
                {
                    return;
                }

                _recoveryScheduled = false;
                StopPlaybackCore();
            }

            Report(VideoWallpaperState.Failed, "The graphics device was lost.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the video after the graphics device was lost failed");
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var detail = string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
        _logger.LogError("The video wallpaper stopped: {Detail} (0x{Code:X8})", detail, args.ExtendedErrorCode?.HResult ?? 0);

        // The file itself is unusable: unlike a lost window, a fresh mount cannot help.
        _playbackDisabled = true;
        Report(VideoWallpaperState.Failed, detail);
    }

    private void Report(VideoWallpaperState state, string? error = null)
    {
        var status = new VideoWallpaperStatus(state, _videoPath, error);
        _started.TrySetResult(status);
        StatusChanged?.Invoke(this, status);
    }
}
