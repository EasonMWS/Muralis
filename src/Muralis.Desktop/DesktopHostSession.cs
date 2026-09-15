using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT;

namespace Muralis.Desktop;

/// <summary>
/// One run of the desktop video host: a Win32 window parented to the desktop worker, a swap chain
/// sized to the primary display, and a media player in frame server mode copying its frames into
/// that swap chain. Everything lives on its own thread with its own message pump, so the UI thread
/// never touches native code and a stuck video cannot freeze the app.
/// </summary>
internal sealed class DesktopHostSession
{
    internal const string WindowClassName = "MuralisDesktopHostWindow";

    /// <summary>DXGI_ERROR_DEVICE_REMOVED / DEVICE_RESET: the video cannot be shown any more.</summary>
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);

    private static readonly object WindowTableGate = new();
    private static readonly Dictionary<nint, DesktopHostSession> LiveWindows = [];
    private static readonly NativeMethods.WindowProc WindowCallback = OnWindowMessage;
    private static nint _instance;
    private static bool _classRegistered;

    private readonly string _videoPath;
    private readonly bool _muted;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<VideoWallpaperStatus> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _renderGate = new();

    private Thread? _thread;
    private uint _threadId;
    private nint _window;
    private nint _worker;
    private NativeMethods.Rect _displayBounds;
    private D3D11Interop.IDXGISwapChainInterop? _swapChain;
    private IDirect3DSurface? _surface;
    private MediaPlayer? _player;
    private nint _device;
    private nint _deviceContext;
    private int _presentFailures;
    private int _frameErrors;
    private long _presentedFrames;
    private bool _playingReported;

    // Shell lifecycle: the host window dies together with Explorer's wallpaper layer, so the
    // session keeps the intent to play and re-mounts itself on the new desktop worker.
    private volatile bool _stopping;
    private volatile bool _mountLost;
    private volatile bool _playbackActive;
    private int _remountAttempts;

    internal DesktopHostSession(string videoPath, bool muted, ILogger logger)
    {
        _videoPath = videoPath;
        _muted = muted;
        _logger = logger;
    }

    /// <summary>How many frames have been put on the desktop since this session started.</summary>
    internal long PresentedFrames => Interlocked.Read(ref _presentedFrames);

    /// <summary>Raised when playback starts, fails or stops. Fires on the host thread.</summary>
    internal event EventHandler<VideoWallpaperStatus>? StatusChanged;

    /// <summary>Starts the host and waits until the first frame is on screen (or the attempt fails).</summary>
    internal async Task<VideoWallpaperStatus> StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { Name = "Muralis desktop host", IsBackground = true };
        _thread.Start();

        try
        {
            return await _started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new VideoWallpaperStatus(
                VideoWallpaperState.Failed,
                _videoPath,
                "The desktop host did not start in time.");
        }
    }

    /// <summary>Posts a stop to the host thread and waits for it to tear everything down.</summary>
    internal async Task StopAsync()
    {
        var thread = _thread;
        _stopping = true;

        // A thread message reaches the host even while its window does not exist — for example
        // after Explorer destroyed the wallpaper layer and the re-mount has not happened yet.
        if (_threadId != 0 && !NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmStopHost, nint.Zero, nint.Zero)
            && thread?.IsAlive == true)
        {
            _logger.LogWarning("The desktop host stop request could not be posted ({Error})", Marshal.GetLastWin32Error());
        }

        if (thread is not null)
        {
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (thread.IsAlive)
            {
                _logger.LogWarning("The desktop host thread did not stop in time");
            }
        }

        _thread = null;
    }

    /// <summary>
    /// Tells the host that the shell restarted and its window is about to disappear. Safe to call
    /// from any thread; the host re-mounts on its own thread, and the five-second safety timer
    /// catches the loss even when this announcement never arrives.
    /// </summary>
    internal void NotifyShellRestarted()
    {
        if (_threadId == 0)
        {
            return;
        }

        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmShellRestarted, nint.Zero, nint.Zero);
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        try
        {
            var worker = DesktopWorkerWindow.FindOrCreate();
            if (worker == nint.Zero)
            {
                throw new InvalidOperationException("The desktop worker window was not found; the video cannot be placed behind the icons.");
            }

            _worker = worker;
            _window = CreateHostWindow(worker);
            RepositionOverPrimaryDisplay();
            CreatePresentation();
            StartPlayback();

            // A thread timer keeps ticking even after the host window is destroyed by an Explorer
            // restart; a window timer would die with it and the re-mount would never run.
            NativeMethods.SetTimer(
                nint.Zero,
                NativeMethods.DisplayChangeCheckTimerId,
                NativeMethods.DisplayChangeCheckIntervalMs,
                nint.Zero);

            RunMessageLoop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop video host failed to start");
            Report(VideoWallpaperState.Failed, ex.Message);
        }
        finally
        {
            TearDown();
        }
    }

    private nint CreateHostWindow(nint worker)
    {
        EnsureWindowClass();

        // Created as a popup and re-parented: that is the combination the shell expects from
        // wallpaper hosts, and it keeps the window out of the taskbar and Alt+Tab.
        var window = NativeMethods.CreateWindowExW(
            NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
            WindowClassName,
            "Muralis",
            NativeMethods.WsPopup,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            _instance,
            nint.Zero);

        if (window == nint.Zero)
        {
            throw new InvalidOperationException($"The desktop host window could not be created (error {Marshal.GetLastWin32Error()}).");
        }

        lock (WindowTableGate)
        {
            LiveWindows[window] = this;
        }

        NativeMethods.SetParent(window, worker);
        return window;
    }

    private void RepositionOverPrimaryDisplay()
    {
        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MonitorDefaultToPrimary);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            throw new InvalidOperationException($"The primary display could not be measured (error {Marshal.GetLastWin32Error()}).");
        }

        _displayBounds = info.Monitor;

        // A child window is positioned in its parent's client space, which is the whole virtual
        // desktop, so screen coordinates have to be shifted by the worker's origin.
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(_worker, ref origin);

        // HWND_TOP keeps the video on top of whatever else draws in the wallpaper layer, while the
        // icons stay visible because their window is above the worker in the shell's own z-order.
        if (!NativeMethods.SetWindowPos(
                _window,
                nint.Zero,
                _displayBounds.Left - origin.X,
                _displayBounds.Top - origin.Y,
                _displayBounds.Width,
                _displayBounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new InvalidOperationException($"The desktop host window could not be placed (error {Marshal.GetLastWin32Error()}).");
        }
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
        _playbackActive = true;

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

    private void StopPlayback()
    {
        lock (_renderGate)
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
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        try
        {
            lock (_renderGate)
            {
                if (_surface is null || _swapChain is null)
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
            _logger.LogError("The desktop swap chain was lost (0x{Result:X8})", result);
            Report(VideoWallpaperState.Failed, "The graphics device was lost.");
            return;
        }

        if (Interlocked.Increment(ref _presentFailures) == 1)
        {
            _logger.LogWarning("Presenting a video frame failed (0x{Result:X8})", result);
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var detail = string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
        _logger.LogError("The video wallpaper stopped: {Detail} (0x{Code:X8})", detail, args.ExtendedErrorCode?.HResult ?? 0);

        // The file itself is unusable: unlike a lost graphics device, re-mounting cannot help.
        _playbackActive = false;
        Report(VideoWallpaperState.Failed, detail);
    }

    private void RunMessageLoop()
    {
        while (true)
        {
            var result = NativeMethods.GetMessageW(out var message, nint.Zero, 0, 0);
            if (result <= 0)
            {
                return;
            }

            // Thread messages (timer, stop, shell restart) arrive with no window at all; they are
            // handled here because the host window may already be gone when they are processed.
            if (message.Hwnd == nint.Zero)
            {
                switch (message.Value)
                {
                    case NativeMethods.WmTimer:
                        OnDisplayCheckTimer();
                        break;

                    case NativeMethods.WmStopHost:
                        HandleStopRequest();
                        break;

                    case NativeMethods.WmShellRestarted:
                        OnShellRestarted();
                        break;
                }

                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    /// <summary>Explorer restarted: re-mount the video on the desktop layer it rebuilt.</summary>
    private void OnShellRestarted()
    {
        if (_stopping)
        {
            return;
        }

        // The announcement can arrive before the old window dies; the timer picks up the loss
        // then. Nothing to do while the current mount is still alive.
        if (_window != nint.Zero && NativeMethods.IsWindow(_window))
        {
            return;
        }

        HandleMountLoss();
        TryRemount();
    }

    /// <summary>
    /// The host window - and with it the desktop parent - is gone. Playback stops, but the intent
    /// to play survives, so the video comes back once the new wallpaper layer is ready.
    /// </summary>
    private void HandleMountLoss()
    {
        var window = _window;
        _window = nint.Zero;
        _worker = nint.Zero;

        if (window != nint.Zero)
        {
            lock (WindowTableGate)
            {
                LiveWindows.Remove(window);
            }
        }

        StopPlayback();
        _playingReported = false;

        if (_mountLost)
        {
            return;
        }

        _mountLost = true;
        _remountAttempts = 0;
        _logger.LogInformation("The desktop host window was lost (Explorer restart?); preparing to re-mount");

        if (_playbackActive)
        {
            // The page shows "starting" until the first frame of the re-mounted video arrives.
            Report(VideoWallpaperState.Starting);
        }
    }

    /// <summary>
    /// Puts the video back on the desktop after the wallpaper layer was recreated. Retried by the
    /// five-second timer, so a slow Explorer restart costs nothing but a few attempts.
    /// </summary>
    private void TryRemount()
    {
        _remountAttempts++;
        try
        {
            var worker = DesktopWorkerWindow.FindOrCreate();
            if (worker == nint.Zero)
            {
                if (_remountAttempts == 1)
                {
                    _logger.LogWarning(
                        "The desktop worker window is not back yet; retrying every {Seconds} s",
                        NativeMethods.DisplayChangeCheckIntervalMs / 1000);
                }

                return;
            }

            _worker = worker;
            _window = CreateHostWindow(worker);
            RepositionOverPrimaryDisplay();
            CreatePresentation();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-mounting the video wallpaper failed (attempt {Attempt})", _remountAttempts);
            RollBackMount();
            return;
        }

        _mountLost = false;
        _logger.LogInformation("The video wallpaper is back on the desktop after {Attempts} attempt(s)", _remountAttempts);
        _remountAttempts = 0;

        if (_playbackActive)
        {
            _playingReported = false;
            StartPlayback();
        }
    }

    /// <summary>Discards a half-built mount so the next attempt starts from a clean state.</summary>
    private void RollBackMount()
    {
        var window = _window;
        _window = nint.Zero;
        _worker = nint.Zero;

        if (window != nint.Zero)
        {
            lock (WindowTableGate)
            {
                LiveWindows.Remove(window);
            }

            NativeMethods.DestroyWindow(window);
        }

        StopPlayback();
    }

    /// <summary>Runs on the host thread when the session was asked to stop.</summary>
    private void HandleStopRequest()
    {
        _stopping = true;
        _playbackActive = false;
        NativeMethods.KillTimer(nint.Zero, NativeMethods.DisplayChangeCheckTimerId);
        StopPlayback();

        var window = _window;
        _window = nint.Zero;
        _worker = nint.Zero;
        if (window != nint.Zero)
        {
            lock (WindowTableGate)
            {
                LiveWindows.Remove(window);
            }

            NativeMethods.DestroyWindow(window);
        }

        NativeMethods.PostQuitMessage(0);
    }

    /// <summary>Rebuilds the swap chain after the display layout changed.</summary>
    private void OnDisplayCheckTimer()
    {
        if (_stopping)
        {
            return;
        }

        if (_mountLost)
        {
            TryRemount();
            return;
        }

        if (_window == nint.Zero || !NativeMethods.IsWindow(_window))
        {
            // Safety net: should the destroy notification have been missed, the dead window is
            // noticed here and treated exactly like a shell restart.
            HandleMountLoss();
            TryRemount();
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MonitorDefaultToPrimary);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        if (info.Monitor.Width == _displayBounds.Width && info.Monitor.Height == _displayBounds.Height)
        {
            return;
        }

        _logger.LogInformation(
            "The display changed to {Width}x{Height}; restarting the video wallpaper",
            info.Monitor.Width,
            info.Monitor.Height);

        StopPlayback();
        _playingReported = false;
        RepositionOverPrimaryDisplay();
        CreatePresentation();
        StartPlayback();
    }

    private void TearDown()
    {
        _stopping = true;
        _playbackActive = false;
        NativeMethods.KillTimer(nint.Zero, NativeMethods.DisplayChangeCheckTimerId);
        StopPlayback();

        var window = _window;
        _window = nint.Zero;
        if (window != nint.Zero)
        {
            lock (WindowTableGate)
            {
                LiveWindows.Remove(window);
            }

            NativeMethods.DestroyWindow(window);
        }

        _worker = nint.Zero;
        _threadId = 0;
        Report(VideoWallpaperState.Stopped);
    }

    private void Report(VideoWallpaperState state, string? error = null)
    {
        var status = new VideoWallpaperStatus(state, _videoPath, error);
        _started.TrySetResult(status);
        StatusChanged?.Invoke(this, status);
    }

    private static void EnsureWindowClass()
    {
        lock (WindowTableGate)
        {
            if (_classRegistered)
            {
                return;
            }

            _instance = NativeMethods.GetModuleHandleW(null);
            var windowClass = new NativeMethods.WindowClass
            {
                WndProc = Marshal.GetFunctionPointerForDelegate(WindowCallback),
                Instance = _instance,
                Background = nint.Zero,
                Cursor = nint.Zero,
                ClassName = WindowClassName,
            };

            if (NativeMethods.RegisterClassW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException($"The desktop host window class could not be registered (error {Marshal.GetLastWin32Error()}).");
            }

            _classRegistered = true;
        }
    }

    private static nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        DesktopHostSession? session;
        lock (WindowTableGate)
        {
            LiveWindows.TryGetValue(hwnd, out session);
        }

        if (session is not null)
        {
            switch (message)
            {
                case NativeMethods.WmStopHost:
                case NativeMethods.WmClose:
                    // Stop the player before the window disappears so no frame is presented into a
                    // swap chain whose window is gone.
                    session.HandleStopRequest();
                    return nint.Zero;

                case NativeMethods.WmDestroy:
                    if (session._stopping)
                    {
                        lock (WindowTableGate)
                        {
                            LiveWindows.Remove(hwnd);
                        }

                        NativeMethods.PostQuitMessage(0);
                    }
                    else
                    {
                        // Explorer took the wallpaper layer down: the window died with it, and the
                        // session re-mounts instead of ending.
                        session.HandleMountLoss();
                    }

                    return nint.Zero;

                case NativeMethods.WmEraseBackground:
                    return 1;

                case NativeMethods.WmTimer:
                    session.OnDisplayCheckTimer();
                    return nint.Zero;
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
