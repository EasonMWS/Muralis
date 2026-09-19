using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Motion;
using Muralis.Desktop.Input;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace Muralis.App.UI.Dock;

/// <summary>
/// The real dock window: a borderless, non-activating strip at the bottom of the screen carrying the
/// pinned applications and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It has no opinion about when it should be up — <see cref="IDockExperienceService"/> decides that and
/// calls in here — and it is deliberately unaware of the desktop mode, so the dock is the same surface
/// on a native desktop and behind Clean Desktop.
/// </para>
/// <para>
/// The window is <b>content-sized</b>: its width follows the icons actually pinned, reported by the
/// surface through <see cref="DockHost.ContentSizeChanged"/>, and its height is the stripe the
/// magnification is drawn in. Two things can change the geometry, and both are structural: the pinned
/// list changing, and the pointer entering or leaving. Nothing on the pointer's own path resizes
/// anything.
/// </para>
/// <para>
/// The Desktop Shelf is not part of this presentation. Its service stays alive and is what a future
/// floating Shelf would be built on; this window simply does not carry it.
/// </para>
/// </remarks>
public sealed class DesktopDockHost : IDesktopDockHost
{
    private readonly IShellIconProvider _icons;
    private readonly IPinnedAppService _pinned;
    private readonly IApplicationLocationRevealer _revealer;
    private readonly IFilePickerService _picker;
    private readonly ILocalizationService _localization;
    private readonly ILogger<PinnedAppsPresenter> _pinnedLog;
    private readonly ILogger<DesktopDockHost> _logger;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly RawPointerBroker _rawPointer;
    private readonly DispatcherQueue _dispatcher;
    private Window? _window;
    private DockHost? _dock;
    private PinnedAppsPresenter? _pinnedPresenter;
    private FrameworkElement? _windowRoot;

    /// <summary>The width the pin run is currently drawn at, in DIP. Zero means nothing is pinned.</summary>
    private double _contentWidthDip;


    private bool _windowShown;

    public DesktopDockHost(
        IShellIconProvider icons,
        IPinnedAppService pinned,
        IApplicationLocationRevealer revealer,
        IFilePickerService picker,
        ILocalizationService localization,
        ILogger<PinnedAppsPresenter> pinnedLog,
        ILogger<DesktopDockHost> logger,
        ISettingsService settings,
        IThemeService theme,
        RawPointerBroker rawPointer)
    {
        _icons = icons;
        _pinned = pinned;
        _revealer = revealer;
        _picker = picker;
        _localization = localization;
        _pinnedLog = pinnedLog;
        _logger = logger;
        _settings = settings;
        _theme = theme;
        _rawPointer = rawPointer;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public bool IsAvailable => _dispatcher is not null;

    public Task PrepareAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        EnsureWindow();
        PositionWindow();
        return Task.CompletedTask;
    });

    public Task ShowAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        EnsureWindow();

        // Nothing pinned is nothing to show. The dock is content-sized, so an empty one would be an empty
        // plate sitting on the desktop; it stays hidden until there is something on it.
        if (_contentWidthDip <= 0)
        {
            HideWindow();
            return Task.CompletedTask;
        }

        PositionWindow();
        _window!.AppWindow.Show(activateWindow: false);
        _windowShown = true;
        return Task.CompletedTask;
    });

    public Task HideAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        HideWindow();
        return Task.CompletedTask;
    });

    public Task CloseAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        _settings.SettingsChanged -= OnSettingsChanged;

        if (_dock is not null)
        {
            _dock.MotionBoundsChanged -= OnDockMotionBoundsChanged;
            _dock.ContentSizeChanged -= OnDockContentSizeChanged;
            _dock.PinnedNotice -= OnPinnedNotice;

            // The magnification owns a pointer subscription and a layout subscription, and neither is given
            // back by the Unloaded event alone: a window that is closing is not guaranteed to raise it. The
            // dock is being torn down for good here, so the coordinator is disposed rather than only stopped.
            _dock.ReleaseMotion();
        }

        // The presenter holds the service subscription the dock was drawn from. Dropping the reference is not
        // the same as letting go of the service, and this presenter never comes back.
        _pinnedPresenter?.Detach();

        // Closed, not hidden: the dock is a window of its own, and the application cannot end while one
        // is still open. The order matters — the caller closes this before the main window, because
        // closing the last window is what takes the message loop with it.
        _window?.Close();
        if (_windowRoot is not null)
        {
            _theme.UnregisterRoot(_windowRoot);
        }

        _window = null;
        _dock = null;
        _pinnedPresenter = null;
        _windowRoot = null;
        _windowShown = false;
        return Task.CompletedTask;
    });

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _pinnedPresenter = new PinnedAppsPresenter(
            _pinned,
            _icons,
            _revealer,
            _picker,
            _localization,
            _pinnedLog,
            DispatcherQueue.GetForCurrentThread());

        _dock = new DockHost
        {
            // Fill the HWND. The dock surface inside DockHost remains bottom-centred and content-sized;
            // filling here keeps the client-pixel/DIP scale truthful and the baseline fixed when the HWND
            // spends its motion reserve.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            PinnedApps = _pinnedPresenter,
            BackgroundStyle = NormalizeBackground(_settings.Current.Dock.BackgroundStyle),
            RawPointerBroker = _rawPointer,
        };
        _dock.PinnedNotice += OnPinnedNotice;

        // The dock decides how wide it is and when it needs the room the magnification is drawn in; the
        // window it lives in is this class's business, so it is told rather than asking.
        _dock.MotionBoundsChanged += OnDockMotionBoundsChanged;
        _dock.ContentSizeChanged += OnDockContentSizeChanged;

        // Pinning is available as soon as the strip exists, and its list arrives without holding up
        // the window: the strip is on screen once there is something to draw on it.
        _pinnedPresenter.Attach();
        _ = RestorePinnedAsync();

        _windowRoot = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { _dock },
        };
        _theme.RegisterRoot(_windowRoot);

        _window = new Window
        {
            Title = "Muralis Dock",
            Content = _windowRoot,
        };
        _window.AppWindow.IsShownInSwitchers = false;

        // The dock's motion measures the cursor against the window it is drawn in, so the window's handle is
        // given to it here rather than looked up later from a point that is covered by the desktop.
        _dock.WindowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(_window.AppWindow.Id);

        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // The Dock is a passive desktop-layer tool window, not a HUD. A normal application that
            // overlaps it must naturally sit above it in z-order.
            presenter.IsAlwaysOnTop = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        ConfigurePassiveToolWindow(_window);

        // Created hidden and off the work area, and shown only once the surface says it has something to
        // draw. The dock is content-sized, so a window with nothing pinned is a small empty plate on the
        // desktop; the list arrives asynchronously and the surface reports its width when it does.
        HideWindow();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var style = NormalizeBackground(settings.Dock.BackgroundStyle);
        if (_dispatcher.HasThreadAccess)
        {
            ApplyBackgroundStyle(style);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyBackgroundStyle(style));
        }
    }

    private void ApplyBackgroundStyle(DockBackgroundStyle style)
    {
        if (_dock is not null)
        {
            _dock.BackgroundStyle = style;
        }
    }

    private static DockBackgroundStyle NormalizeBackground(DockBackgroundStyle style) =>
        style == DockBackgroundStyle.Glass ? DockBackgroundStyle.Glass : DockBackgroundStyle.Transparent;

    /// <summary>
    /// Keeps the borderless window out of Alt+Tab and prevents pointer interaction from stealing
    /// foreground activation. It remains an ordinary top-level window: no owner, WorkerW parent,
    /// TOPMOST bit or foreground-window polling is used.
    /// </summary>
    private static void ConfigurePassiveToolWindow(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style &= ~WsExTopmost;
        style |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new nint(style));
    }

    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private async Task RestorePinnedAsync()
    {
        try
        {
            await _pinnedPresenter!.RestoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The pinned applications could not be restored in the dock");
        }
    }

    private void OnPinnedNotice(object? sender, string message) =>
        _logger.LogInformation("The dock reported: {Message}", message);

    /// <summary>
    /// The icons the dock is actually drawing have changed width: a pin was added, removed or reordered.
    /// This is the structural trigger for a resize, and it is the only thing besides a pointer transition
    /// that can move the window.
    /// </summary>
    private void OnDockContentSizeChanged(object? sender, double contentWidthDip)
    {
        var changed = Math.Abs(contentWidthDip - _contentWidthDip) >= 0.5;
        _contentWidthDip = contentWidthDip;

        if (!changed || _window is null)
        {
            return;
        }

        // Nothing pinned: there is nothing to draw, so the window goes away rather than becoming a plate
        // with no icons on it. The dock is still "on" as far as the user's preference is concerned.
        if (_contentWidthDip <= 0)
        {
            HideWindow();
            return;
        }

        // The first pin on an empty dock is also the moment the window has something to show.
        if (!_windowShown)
        {
            PositionWindow();
            _window.AppWindow.Show(activateWindow: false);
            _windowShown = true;
            return;
        }

        PositionWindow();
    }

    private void HideWindow()
    {
        if (_window is null)
        {
            return;
        }

        // Hidden and taken off the work area, not merely hidden. A borderless WS_EX_NOACTIVATE tool window
        // is not guaranteed to stay un-drawn the first time it is told to hide, and the dock's empty state
        // is the one state where a stray window would be a plate with nothing on it.
        _window.AppWindow.Hide();
        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        var width = (int)Math.Ceiling(DockStripeGeometry.MinimumPlateWidthDip * scale);
        var height = (int)Math.Ceiling(DockStripeGeometry.StripeHeightDip * scale);

        // Above the work area is off-screen for a bottom-anchored strip: the work area's top is under the
        // taskbar, so a window ending there is drawn nowhere the user can see.
        _window.AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y - height, width, height));
        _windowShown = false;
    }

    /// <summary>
    /// Puts the dock strip where it belongs: centred along the bottom of the display's work area, sized to
    /// the apps pinned to it, at the resting size or at the size the motion needs — whichever the dock is
    /// asking for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DockMotionBounds"/> owns both sizes and both placements; this only spends them. The window
    /// is anchored by its bottom edge in either size and the dock's surface sits on the bottom of the window,
    /// so growing the window takes its room from above and from the sides, and the dock does not move.
    /// </para>
    /// <para>
    /// This runs when the dock is shown, when the pinned list changes width, and on the two transitions the
    /// pointer causes — onto the dock and off it. It never runs from a pointer move: a window resize on the
    /// pointer path would be both a layout storm and a visible hitch.
    /// </para>
    /// </remarks>
    private void PositionWindow()
    {
        if (_window is null)
        {
            return;
        }

        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        var iconCount = _pinnedPresenter?.Items.Count ?? 0;
        var bounds = _dock is { IsMotionExpanded: true }
            ? DockMotionBounds.Expanded(_contentWidthDip, iconCount, area, scale)
            : DockMotionBounds.Resting(_contentWidthDip, area, scale);

        _window.AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    /// <summary>
    /// Gives the motion its room when the pointer arrives and takes it back when the pointer leaves.
    /// </summary>
    /// <remarks>
    /// The dock decides; this only resizes the window it lives in. The window grows once on the way in and once
    /// on the way out, never per move, which is what keeps the pointer path free of layout work.
    /// </remarks>
    private void OnDockMotionBoundsChanged(object? sender, bool expanded) => PositionWindow();

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }
}
