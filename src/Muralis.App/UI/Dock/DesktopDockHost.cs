using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;
using Windows.Graphics;

namespace Muralis.App.UI.Dock;

/// <summary>
/// The real dock window: a borderless, always-on-top strip at the bottom of the screen carrying the
/// pinned applications, the Shelf and the utilities.
/// </summary>
/// <remarks>
/// It has no opinion about when it should be up — <see cref="IDockExperienceService"/> decides that and
/// calls in here — and it is deliberately unaware of the desktop mode, so the dock is the same surface
/// on a native desktop and behind Clean Desktop.
/// </remarks>
public sealed class DesktopDockHost : IDesktopDockHost
{
    private readonly IDesktopShelfService _shelf;
    private readonly IShellIconProvider _icons;
    private readonly IPinnedAppService _pinned;
    private readonly IApplicationLocationRevealer _revealer;
    private readonly IFilePickerService _picker;
    private readonly ILocalizationService _localization;
    private readonly ILogger<PinnedAppsPresenter> _pinnedLog;
    private readonly ILogger<DesktopDockHost> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<DesktopShelfViewItem> _items = [];
    private Window? _window;
    private DockHost? _dock;
    private PinnedAppsPresenter? _pinnedPresenter;
    private bool _subscribed;

    public DesktopDockHost(
        IDesktopShelfService shelf,
        IShellIconProvider icons,
        IPinnedAppService pinned,
        IApplicationLocationRevealer revealer,
        IFilePickerService picker,
        ILocalizationService localization,
        ILogger<PinnedAppsPresenter> pinnedLog,
        ILogger<DesktopDockHost> logger)
    {
        _shelf = shelf;
        _icons = icons;
        _pinned = pinned;
        _revealer = revealer;
        _picker = picker;
        _localization = localization;
        _pinnedLog = pinnedLog;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public bool IsAvailable => _dispatcher is not null;

    public Task PrepareAsync(CancellationToken cancellationToken = default) =>
        RunOnUiAsync(() => PrepareOnUiAsync(_shelf.Snapshot, cancellationToken));

    public Task ShowAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        EnsureWindow();
        PositionWindow();
        _window!.AppWindow.Show(activateWindow: false);
        return Task.CompletedTask;
    });

    public Task HideAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        _window?.AppWindow.Hide();
        return Task.CompletedTask;
    });

    public Task CloseAsync(CancellationToken cancellationToken = default) => RunOnUiAsync(() =>
    {
        if (_subscribed)
        {
            _shelf.Changed -= OnShelfChanged;
            _subscribed = false;
        }

        // Closed, not hidden: the dock is a window of its own, and the application cannot end while one
        // is still open. The order matters — the caller closes this before the main window, because
        // closing the last window is what takes the message loop with it.
        _window?.Close();
        _window = null;
        _dock = null;
        _pinnedPresenter = null;
        return Task.CompletedTask;
    });

    private async Task PrepareOnUiAsync(DesktopShelfSnapshot snapshot, CancellationToken cancellationToken)
    {
        EnsureWindow();
        _items.Clear();
        foreach (var item in snapshot.Items)
        {
            _items.Add(new DesktopShelfViewItem(item));
        }

        PositionWindow();

        foreach (var item in _items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await item.LoadIconAsync(_icons, cancellationToken);
        }
    }

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
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            PinnedApps = _pinnedPresenter,
            ShelfItems = _items,
            UtilityItems = UtilityItems,
        };
        _dock.ShelfItemInvoked += OnShelfItemInvoked;
        _dock.PinnedAddRequested += OnPinnedAddRequested;
        _dock.PinnedNotice += OnPinnedNotice;

        // Pinning is available as soon as the strip exists, and its list arrives without holding up
        // the window: the strip is on screen with an empty pinned zone rather than not on screen.
        _pinnedPresenter.Attach();
        _ = RestorePinnedAsync();

        _window = new Window
        {
            Title = "Muralis Dock",
            Content = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Children = { _dock },
            },
        };
        _window.AppWindow.IsShownInSwitchers = false;
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        if (!_subscribed)
        {
            _shelf.Changed += OnShelfChanged;
            _subscribed = true;
        }
    }

    private async Task RestorePinnedAsync()
    {
        try
        {
            await _pinnedPresenter!.RestoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The pinned applications could not be restored in the Shelf");
        }
    }

    private async void OnPinnedAddRequested(object? sender, EventArgs args)
    {
        try
        {
            if (_pinnedPresenter is not null && await _pinnedPresenter.AddFromPickerAsync() is { } message)
            {
                _logger.LogInformation("The Shelf pinned an application: {Message}", message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pinning an application from the Shelf failed");
        }
    }

    private void OnPinnedNotice(object? sender, string message) =>
        _logger.LogInformation("The Shelf reported: {Message}", message);

    private void PositionWindow()
    {
        if (_window is null)
        {
            return;
        }

        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(960, Math.Max(520, area.Width - 32));
        const int height = 116;
        _window.AppWindow.MoveAndResize(new RectInt32(
            area.X + ((area.Width - width) / 2),
            area.Y + area.Height - height - 16,
            width,
            height));
    }

    private void OnShelfChanged(object? sender, DesktopShelfSnapshot snapshot) =>
        _dispatcher.TryEnqueue(async () => await PrepareOnUiAsync(snapshot, CancellationToken.None));

    private async void OnShelfItemInvoked(object? sender, DesktopShelfItem item)
    {
        try
        {
            await _shelf.OpenAsync(item);
        }
        catch
        {
            // The source can disappear between the watcher event and the double-click. The next
            // coalesced refresh removes it; one broken item must not affect the rest of the Shelf.
        }
    }

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

    private static IReadOnlyList<DockShellItem> UtilityItems { get; } =
    [
        new("settings", "Settings", "\uE713", "muralis:settings", DockShellItemType.Utility),
        new("desktop", "Desktop", "\uE7F4", "shell:desktop", DockShellItemType.Utility),
    ];
}
