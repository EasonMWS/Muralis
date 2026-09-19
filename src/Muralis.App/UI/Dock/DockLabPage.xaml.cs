using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;

namespace Muralis.App.UI.Dock;

public sealed partial class DockLabPage : Page
{
    private readonly IDesktopShelfService _shelf = App.GetService<IDesktopShelfService>();
    private readonly IShellIconProvider _icons = App.GetService<IShellIconProvider>();
    private readonly ICleanDesktopPresentation _cleanDesktop = App.GetService<ICleanDesktopPresentation>();
    private readonly IPinnedAppService _pinned = App.GetService<IPinnedAppService>();
    private readonly IApplicationLocationRevealer _revealer = App.GetService<IApplicationLocationRevealer>();
    private readonly IFilePickerService _files = App.GetService<IFilePickerService>();
    private readonly ILocalizationService _localization = App.GetService<ILocalizationService>();
    private readonly ILogger<PinnedAppsPresenter> _pinnedLog = App.GetService<ILogger<PinnedAppsPresenter>>();
    private bool _realMode = true;

    public DockLabPage()
    {
        PinnedApps = new PinnedAppsPresenter(
            _pinned,
            _icons,
            _revealer,
            _files,
            _localization,
            _pinnedLog,
            DispatcherQueue.GetForCurrentThread());

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public PinnedAppsPresenter PinnedApps { get; }

    public ObservableCollection<DesktopShelfViewItem> ShelfItems { get; } = [];

    public IReadOnlyList<DockShellItem> UtilityItems { get; } =
    [
        Utility("settings", "Settings", "\uE713"),
        Utility("trash", "Trash", "\uE74D"),
        Utility("desktop", "Desktop", "\uE7F4"),
    ];

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _shelf.Changed += OnShelfChanged;
        Dock.PinnedNotice += OnPinnedNotice;
        PinnedApps.Attach();
        await PinnedApps.RestoreAsync();
        await ShowRealAsync(await _shelf.StartAsync());
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _shelf.Changed -= OnShelfChanged;
        Dock.PinnedNotice -= OnPinnedNotice;
        PinnedApps.Detach();
    }

    private async void OnShelfModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ShelfMode.SelectedIndex < 0)
        {
            return;
        }

        _realMode = ShelfMode.SelectedIndex == 1;
        if (_realMode)
        {
            await ShowRealAsync(await _shelf.RefreshAsync());
        }
        else
        {
            ShowMock();
        }
    }

    private void OnShelfChanged(object? sender, DesktopShelfSnapshot snapshot)
    {
        if (_realMode)
        {
            DispatcherQueue.TryEnqueue(async () => await ShowRealAsync(snapshot));
        }
    }

    private async Task ShowRealAsync(DesktopShelfSnapshot snapshot)
    {
        ShelfItems.Clear();
        foreach (var item in snapshot.Items)
        {
            ShelfItems.Add(new DesktopShelfViewItem(item));
        }

        UpdateDiagnostics(snapshot);
        foreach (var item in ShelfItems.ToArray())
        {
            await item.LoadIconAsync(_icons);
        }
    }

    private void ShowMock()
    {
        ShelfItems.Clear();
        var mock = new[]
        {
            new DesktopShelfItem("mock:folder", "Projects", string.Empty, DockShellItemType.Folder, DesktopShelfItemSource.Shell),
            new DesktopShelfItem("mock:shortcut", "Launch Pad", string.Empty, DockShellItemType.Shortcut, DesktopShelfItemSource.Shell),
            new DesktopShelfItem("mock:image", "Aurora.png", string.Empty, DockShellItemType.File, DesktopShelfItemSource.Shell),
            new DesktopShelfItem("mock:long", "非常长的文件名 with emoji 🌌 and multiple.dots.final.docx", string.Empty, DockShellItemType.File, DesktopShelfItemSource.Shell),
        };
        foreach (var item in mock)
        {
            ShelfItems.Add(new DesktopShelfViewItem(item));
        }

        Diagnostics.Text = "Mode: Mock · 4 items · real Desktop is not being projected";
    }

    /// <summary>
    /// Pinning starts from one place: the picker. Explorer drop and the picker both end up in the
    /// same call, so an app can only be pinned in one way, with one set of rules. The shipping dock has
    /// no add affordance at all — this is the lab page's own button, and the product's add path is the
    /// Dock page in the app's settings.
    /// </summary>
    private async void OnPinnedAddRequested(object sender, RoutedEventArgs args)
    {
        if (await PinnedApps.AddFromPickerAsync() is { } message)
        {
            PinnedStatus.Text = message;
        }
    }

    private void OnPinnedNotice(object? sender, string message) => PinnedStatus.Text = message;

    private void UpdateDiagnostics(DesktopShelfSnapshot snapshot)
    {
        Diagnostics.Text =
            $"Mode: Real Desktop · Items {snapshot.Items.Count} · User {snapshot.UserCount} · Public {snapshot.PublicCount} · " +
            $"Icon cache {_icons.CacheCount} · Clean Desktop {(_cleanDesktop.IsNativeDesktopHidden ? "active" : "inactive")} · " +
            $"Last refresh {snapshot.RefreshedAt:HH:mm:ss} ({snapshot.EnumerationDuration.TotalMilliseconds:0.0} ms)";
    }

    private static DockShellItem Utility(string id, string name, string glyph) => new(
        id, name, glyph, $"mock:utility:{id}", DockShellItemType.Utility);
}
