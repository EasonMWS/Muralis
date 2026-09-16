using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Muralis.Core.Dock;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>
/// Drives the dynamic wallpaper page: pick a video, put it on the desktop behind the icons,
/// take it off again, decide whether it comes back on the next launch, and choose what the desktop
/// itself is — Windows' own, a preview of the canvas over it, or a takeover with the canvas as the
/// way in. Also where the items on that desktop are added, removed and refreshed.
/// </summary>
public sealed partial class DynamicWallpaperViewModel : ViewModelBase
{
    /// <summary>How many skipped entries the first-run dialog lists before it only counts them.</summary>
    private const int UnsupportedListLimit = 8;

    private readonly IVideoWallpaperService _videoWallpaper;
    private readonly IDesktopCanvasService _canvas;
    private readonly IDesktopModeService _desktopMode;
    private readonly IDesktopItemSyncService _desktopSync;
    private readonly ISettingsService _settingsService;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger<DynamicWallpaperViewModel> _logger;
    private bool _applyingSettings = true;
    private bool _applyingMode;
    private bool _applyingDock;
    private VideoWallpaperState _state;
    private (string Key, object?[] Args)? _message;
    private DispatcherQueueTimer? _diagnosticsTimer;

    [ObservableProperty]
    public partial string VideoPath { get; set; }

    [ObservableProperty]
    public partial bool Muted { get; set; }

    [ObservableProperty]
    public partial bool StartWithApp { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Live state of the desktop video, shown as the playback card's caption.</summary>
    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    /// <summary>Which of <see cref="Modes"/> the user picked. The wish, not what is happening.</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    /// <summary>What the desktop is really doing, shown as the desktop card's caption.</summary>
    [ObservableProperty]
    public partial string DesktopStateText { get; set; }

    /// <summary>True while a mode change is in flight, so the picker waits for it.</summary>
    [ObservableProperty]
    public partial bool IsDesktopBusy { get; set; }

    /// <summary>
    /// Whether the native desktop is still owed a give-back. Shown as a warning with the one action
    /// that always works, whether or not the canvas or the layout can be used.
    /// </summary>
    [ObservableProperty]
    public partial bool NeedsDesktopRecovery { get; set; }

    /// <summary>True while a refresh of the user's own desktop items is running.</summary>
    [ObservableProperty]
    public partial bool IsRefreshingItems { get; set; }

    /// <summary>What the user typed into the address box; cleared once the address is on the canvas.</summary>
    [ObservableProperty]
    public partial string NewItemUrl { get; set; }

    /// <summary>True while an import or a removal is in flight, so the buttons wait for it.</summary>
    [ObservableProperty]
    public partial bool IsCanvasItemsBusy { get; set; }

    /// <summary>Whether the edge dock is switched on. It lives in the desktop layout, not in settings.</summary>
    [ObservableProperty]
    public partial bool DockEnabled { get; set; }

    /// <summary>Whether the rail retracts when the pointer is away from it.</summary>
    [ObservableProperty]
    public partial bool DockAutoHide { get; set; }

    /// <summary>Which of <see cref="DockEdges"/> the dock hugs.</summary>
    [ObservableProperty]
    public partial int DockEdgeIndex { get; set; }

    /// <summary>Whether the development diagnostics panel is expanded.</summary>
    [ObservableProperty]
    public partial bool IsDiagnosticsOpen { get; set; }

    /// <summary>The latest canvas measurements, one field per line; development builds only.</summary>
    [ObservableProperty]
    public partial string DiagnosticsText { get; set; }

    public DynamicWallpaperViewModel(
        IVideoWallpaperService videoWallpaper,
        IDesktopCanvasService canvas,
        IDesktopModeService desktopMode,
        IDesktopItemSyncService desktopSync,
        ISettingsService settingsService,
        IFilePickerService filePicker,
        IDialogService dialogs,
        ILocalizationService localization,
        ILogger<DynamicWallpaperViewModel> logger)
        : base(localization)
    {
        _videoWallpaper = videoWallpaper;
        _canvas = canvas;
        _desktopMode = desktopMode;
        _desktopSync = desktopSync;
        _settingsService = settingsService;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        CanvasItems = [];
        DockEdges = [];
        Modes = [];
        NewItemUrl = string.Empty;

        foreach (var edge in DockEdgeInfo.All)
        {
            DockEdges.Add(new DesktopDockEdgeRow(edge, localization));
        }

        ReloadModeRows();

        var saved = settingsService.Current.VideoWallpaper;
        VideoPath = saved.VideoPath;
        Muted = saved.Muted;
        StartWithApp = saved.Enabled;
        StateText = string.Empty;

        _videoWallpaper.StatusChanged += OnStatusChanged;
        ApplyStatus(_videoWallpaper.Status);

        // The picker starts where the service already is — the app may have restored the desktop
        // before this page was ever opened.
        _desktopMode.Changed += OnDesktopChanged;
        ApplyDesktopStatus(_desktopMode.Status);
        DiagnosticsText = string.Empty;
        _applyingSettings = false;

        // The list is what the canvas holds right now, which the page reads back on the pool.
        _ = ReloadCanvasItemsAsync();
    }

    public string? SuccessMessage => StatusIsError ? null : StatusMessage;

    public string? ErrorMessage => StatusIsError ? StatusMessage : null;

    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoPath);

    public string VideoFileName => HasVideo ? Path.GetFileName(VideoPath) : Loc.Get("Dynamic_NoVideo");

    public bool IsPlaying => _state == VideoWallpaperState.Playing;

    public bool CanPlay => HasVideo && !IsBusy && _state is VideoWallpaperState.Stopped or VideoWallpaperState.Failed;

    public bool CanStop => !IsBusy && _state is VideoWallpaperState.Starting or VideoWallpaperState.Playing;

    public bool CanToggleCanvas => !IsDesktopBusy;

    /// <summary>The items on the canvas, in the order the canvas keeps them.</summary>
    public ObservableCollection<DesktopItemRow> CanvasItems { get; }

    /// <summary>The three desktop modes, in the order the picker offers them.</summary>
    public ObservableCollection<DesktopModeRow> Modes { get; }

    /// <summary>The four display edges, in the order the picker offers them.</summary>
    public ObservableCollection<DesktopDockEdgeRow> DockEdges { get; }

    /// <summary>Whether there is anything to list yet; the empty hint shows until there is.</summary>
    public bool HasCanvasItems => CanvasItems.Count > 0;

    /// <summary>The import buttons wait while an import or a removal is running.</summary>
    public bool CanEditCanvasItems => !IsCanvasItemsBusy && !IsRefreshingItems;

    /// <summary>The refresh button waits while a refresh is running.</summary>
    public bool CanRefreshItems => !IsRefreshingItems && !IsCanvasItemsBusy;

    /// <summary>The diagnostics panel is a development tool; release builds do not offer it.</summary>
    public bool IsDiagnosticsAvailable =>
#if DEBUG
        true;
#else
        false;
#endif

    public override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(VideoFileName));
        StateText = DescribeState(_videoWallpaper.Status);
        ReloadModeRows();
        OnPropertyChanged(nameof(CanToggleCanvas));
        DesktopStateText = DescribeDesktopStatus(_desktopMode.Status);

        if (_message is { } message)
        {
            StatusMessage = Loc.Format(message.Key, message.Args);
        }
    }

    public override void DetachFromPage()
    {
        _videoWallpaper.StatusChanged -= OnStatusChanged;
        _desktopMode.Changed -= OnDesktopChanged;
        StopDiagnostics();
        base.DetachFromPage();
    }

    [RelayCommand]
    private async Task ChooseVideoAsync()
    {
        var path = await _filePicker.PickVideoFileAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        VideoPath = path;
        _settingsService.Update(settings => settings.VideoWallpaper.VideoPath = path);
        SetMessage("Dynamic_Status_VideoSelected", isError: false, Path.GetFileName(path));

        // Swapping the clip while one is on the desktop: show the new one right away.
        if (CanStop)
        {
            await PlayAsync();
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (!HasVideo)
        {
            SetMessage("Dynamic_Status_ChooseFirst", isError: true);
            return;
        }

        if (!File.Exists(VideoPath))
        {
            SetMessage("Dynamic_Status_FileMissing", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            _settingsService.Update(settings =>
            {
                settings.VideoWallpaper.VideoPath = VideoPath;
                settings.VideoWallpaper.Muted = Muted;
                settings.VideoWallpaper.Enabled = true;
            });

            ApplyStartWithApp(true);
            ApplyStatus(new VideoWallpaperStatus(VideoWallpaperState.Starting, VideoPath));
            ApplyStatus(await _videoWallpaper.StartAsync(VideoPath, Muted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The video wallpaper could not be started");
            SetMessage("Dynamic_Status_StartFailed", isError: true, ex.Message);
            ApplyStatus(new VideoWallpaperStatus(VideoWallpaperState.Failed, VideoPath, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            _settingsService.Update(settings => settings.VideoWallpaper.Enabled = false);
            ApplyStartWithApp(false);
            await _videoWallpaper.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The video wallpaper could not be removed from the desktop");
            SetMessage("Dynamic_Status_StopFailed", isError: true, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnVideoPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(VideoFileName));
        OnPropertyChanged(nameof(CanPlay));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanStop));
    }

    partial void OnMutedChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.VideoWallpaper.Muted = value);

        // The frame server has no live mute switch; a running video is restarted muted.
        if (CanStop)
        {
            _ = PlayAsync();
        }
    }

    partial void OnStartWithAppChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.VideoWallpaper.Enabled = value);
        SetMessage(value ? "Dynamic_Status_StartupOn" : "Dynamic_Status_StartupOff", isError: false);
    }

    partial void OnModeIndexChanged(int value)
    {
        if (_applyingSettings || _applyingMode)
        {
            return;
        }

        _ = ApplyDesktopModeAsync();
    }

    partial void OnIsDesktopBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggleCanvas));

    partial void OnIsCanvasItemsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditCanvasItems));
        OnPropertyChanged(nameof(CanRefreshItems));
    }

    partial void OnIsRefreshingItemsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditCanvasItems));
        OnPropertyChanged(nameof(CanRefreshItems));
    }

    partial void OnDockEnabledChanged(bool value) => ApplyDock();

    partial void OnDockAutoHideChanged(bool value) => ApplyDock();

    partial void OnDockEdgeIndexChanged(int value) => ApplyDock();

    /// <summary>
    /// Writes the dock's settings back. The dock lives in the desktop layout document, so nothing
    /// here touches <c>settings.json</c>; the change is saved whether the canvas is showing or not.
    /// </summary>
    private void ApplyDock()
    {
        if (_applyingSettings || _applyingDock)
        {
            return;
        }

        _ = ApplyDockAsync();
    }

    private async Task ApplyDockAsync()
    {
        var edge = DockEdgeIndex >= 0 && DockEdgeIndex < DockEdges.Count
            ? DockEdges[DockEdgeIndex].Edge
            : Muralis.Core.Dock.DockEdge.Left;

        try
        {
            var dock = await _canvas.GetDockAsync();
            dock.Enabled = DockEnabled;
            dock.AutoHide = DockAutoHide;
            dock.Edge = edge;

            if (!await _canvas.UpdateDockAsync(dock))
            {
                SetMessage("Dynamic_Canvas_Status_ChangeFailed", isError: true, "dock");
                await ReloadDockAsync();
                return;
            }

            SetMessage("Dynamic_Dock_Status_Changed", isError: false, _canvas.Status.ItemCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The dock settings could not be written");
            SetMessage("Dynamic_Canvas_Status_ChangeFailed", isError: true, ex.Message);
        }
    }

    /// <summary>Reads the dock back, so the controls show what the document really says.</summary>
    private async Task ReloadDockAsync()
    {
        try
        {
            var dock = await _canvas.GetDockAsync();

            _applyingDock = true;
            try
            {
                DockEnabled = dock.Enabled;
                DockAutoHide = dock.AutoHide;
                var index = DockEdges.ToList().FindIndex(row => row.Edge == dock.Edge);
                DockEdgeIndex = index >= 0 ? index : 0;
            }
            finally
            {
                _applyingDock = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The dock settings could not be read");
        }
    }

    partial void OnIsDiagnosticsOpenChanged(bool value)
    {
        if (value)
        {
            StartDiagnostics();
        }
        else
        {
            StopDiagnostics();
        }
    }

    /// <summary>
    /// Puts the desktop into the mode the picker is on. Entering a mode that shows the canvas goes
    /// through the first-run preview first, which is the only place the user is told what is about to be
    /// brought across before it is; leaving them alone means the picker goes back to where it was.
    /// </summary>
    private async Task ApplyDesktopModeAsync()
    {
        if (ModeIndex < 0 || ModeIndex >= Modes.Count)
        {
            return;
        }

        var mode = Modes[ModeIndex].Mode;
        if (mode == _desktopMode.Status.Mode)
        {
            return;
        }

        IsDesktopBusy = true;
        try
        {
            if (mode != DesktopMode.Native && !await ConfirmDesktopPreviewAsync())
            {
                ApplyDesktopStatus(_desktopMode.Status);
                return;
            }

            var status = await _desktopMode.ApplyAsync(mode);
            ApplyDesktopStatus(status);

            if (status.HasError)
            {
                SetMessage("Dynamic_Desktop_Status_Failed", isError: true, status.Error ?? string.Empty);
            }
            else
            {
                SetMessage("Dynamic_Desktop_Status_Changed", isError: false, Modes[ModeIndex].Label);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop could not be put into {Mode}", mode);
            SetMessage("Dynamic_Desktop_Status_Failed", isError: true, ex.Message);
            ApplyDesktopStatus(_desktopMode.Status);
        }
        finally
        {
            IsDesktopBusy = false;
        }
    }

    /// <summary>
    /// The emergency way out, offered whenever the desktop is owed a give-back and reachable whether or
    /// not the canvas or the layout can be used. It is also the honest way to leave a takeover that is
    /// in a state the picker cannot describe.
    /// </summary>
    [RelayCommand]
    private async Task RestoreWindowsDesktopAsync()
    {
        IsDesktopBusy = true;
        try
        {
            var status = await _desktopMode.RestoreNativeDesktopAsync();
            ApplyDesktopStatus(status);

            if (status.HasError)
            {
                SetMessage("Dynamic_Desktop_Status_Failed", isError: true, status.Error ?? string.Empty);
            }
            else
            {
                SetMessage("Dynamic_Desktop_Status_Restored", isError: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Windows desktop could not be restored");
            SetMessage("Dynamic_Desktop_Status_Failed", isError: true, ex.Message);
        }
        finally
        {
            IsDesktopBusy = false;
        }
    }

    /// <summary>
    /// Brings across whatever the user has put on their own desktop since the last look. Nothing is
    /// moved or rewritten: every entry becomes an item that points at where the file already is.
    /// </summary>
    [RelayCommand]
    private async Task RefreshDesktopItemsAsync()
    {
        IsRefreshingItems = true;
        try
        {
            var result = await _desktopSync.SyncAsync();

            if (result.Added.Count == 0)
            {
                SetMessage("Dynamic_Desktop_Status_NothingNew", isError: false);
            }
            else
            {
                SetMessage("Dynamic_Desktop_Status_ItemsAdded", isError: false, result.Added.Count);
            }

            await ReloadCanvasItemsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The user's own desktop could not be read");
            SetMessage("Dynamic_Desktop_Status_RefreshFailed", isError: true, ex.Message);
        }
        finally
        {
            IsRefreshingItems = false;
        }
    }

    /// <summary>
    /// Shows what a first switch to a showing mode would do, and asks. Only entries that will really be
    /// added are counted, and the ones that cannot be shown are named so the user is not left wondering
    /// why something is missing.
    /// </summary>
    private async Task<bool> ConfirmDesktopPreviewAsync()
    {
        if (_desktopMode.Status.IsShowingCanvas)
        {
            // The desktop is already Muralis's; this is a switch between two showing modes, and nothing
            // is adopted again on the way.
            return true;
        }

        DesktopAdoptionPlan plan;
        try
        {
            plan = await _desktopSync.PreviewAsync();
        }
        catch (Exception ex)
        {
            // A desktop that could not be read is not a reason to refuse the mode: it simply means
            // nothing is brought across this time.
            _logger.LogWarning(ex, "The user's own desktop could not be read before switching the mode");
            return true;
        }

        if (plan.IsEmpty)
        {
            return true;
        }

        var report = new StringBuilder();
        report.AppendLine(Loc.Format("Dynamic_Desktop_Preview_Summary", plan.ToAdopt.Count, plan.AlreadyAdopted.Count));
        report.AppendLine(Loc.Format("Dynamic_Desktop_Preview_Declined", plan.Declined.Count));

        if (plan.Unsupported.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(Loc.Get("Dynamic_Desktop_Preview_Unsupported"));

            foreach (var skipped in plan.Unsupported.Take(UnsupportedListLimit))
            {
                report.AppendLine($"  • {skipped.Name} — {skipped.Reason}");
            }

            if (plan.Unsupported.Count > UnsupportedListLimit)
            {
                report.AppendLine(Loc.Format("Dynamic_Desktop_Preview_MoreUnsupported", plan.Unsupported.Count - UnsupportedListLimit));
            }
        }

        if (plan.UnreadableFolders.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(string.Join(Environment.NewLine, plan.UnreadableFolders));
        }

        report.AppendLine();
        report.Append(Loc.Get("Dynamic_Desktop_Preview_Ownership"));

        return await _dialogs.ShowConfirmAsync(
            Loc.Get("Dynamic_Desktop_Preview_Title"),
            report.ToString().TrimEnd(),
            Loc.Get("Dynamic_Desktop_Preview_Continue"),
            Loc.Get("Common_Cancel"));
    }

    /// <summary>
    /// Imports the program or shortcut the user picked. Only what was picked is referenced: nothing
    /// is copied or moved, and the user's own desktop is never scanned or touched.
    /// </summary>
    [RelayCommand]
    private async Task AddApplicationAsync()
    {
        var path = await _filePicker.PickApplicationFileAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await ImportAsync(() => DesktopItemFactory.CreateFromPath(path));
    }

    /// <summary>Imports the folder the user picked; double-clicking it on the canvas opens Explorer.</summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await ImportAsync(() => DesktopItemFactory.CreateFromPath(path));
    }

    /// <summary>Imports the address in the box; double-clicking it opens the default browser.</summary>
    [RelayCommand]
    private async Task AddUrlAsync()
    {
        var url = NewItemUrl?.Trim() ?? string.Empty;
        if (url.Length == 0)
        {
            return;
        }

        await ImportAsync(() => DesktopItemFactory.CreateFromUrl(url));
    }

    /// <summary>
    /// Takes an item off the canvas. Only the canvas layout is edited — the file, shortcut or folder
    /// the item points at is left exactly where it is.
    /// </summary>
    [RelayCommand]
    private async Task RemoveItemAsync(DesktopItemRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsCanvasItemsBusy = true;
        try
        {
            await _canvas.RemoveItemAsync(row.Id);
            CanvasItems.Remove(row);
            OnPropertyChanged(nameof(HasCanvasItems));
            SetMessage("Dynamic_Canvas_Status_ItemRemoved", isError: false, row.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop item {Id} could not be taken off the canvas", row.Id);
            SetMessage("Dynamic_Canvas_Status_ChangeFailed", isError: true, ex.Message);
        }
        finally
        {
            IsCanvasItemsBusy = false;
        }
    }

    private async Task ImportAsync(Func<DesktopItem> create)
    {
        DesktopItem item;
        try
        {
            item = create();
        }
        catch (ArgumentException ex)
        {
            // What was picked or typed cannot be an item; the factory's sentence says why.
            SetMessage("Dynamic_Canvas_Status_ChangeFailed", isError: true, ex.Message);
            return;
        }

        IsCanvasItemsBusy = true;
        try
        {
            if (!await _canvas.AddItemAsync(item))
            {
                SetMessage("Dynamic_Canvas_Status_ItemExists", isError: true);
                return;
            }

            CanvasItems.Add(new DesktopItemRow(item, isMissing: false, RemoveItemAsync));
            OnPropertyChanged(nameof(HasCanvasItems));
            NewItemUrl = string.Empty;
            SetMessage("Dynamic_Canvas_Status_ItemAdded", isError: false, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop item {Id} could not be added to the canvas", item.Id);
            SetMessage("Dynamic_Canvas_Status_ChangeFailed", isError: true, ex.Message);
        }
        finally
        {
            IsCanvasItemsBusy = false;
        }
    }

    /// <summary>
    /// Reads the item list back from the canvas. Whether each target is still there is a file-system
    /// question, so the rows are built on the pool — a folder on a sleeping drive must not stall the
    /// page — and only the finished list reaches the UI thread.
    /// </summary>
    private async Task ReloadCanvasItemsAsync()
    {
        IReadOnlyList<DesktopItem> items;
        try
        {
            items = await _canvas.GetItemsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop canvas items could not be read");
            return;
        }

        var rows = await Task.Run(() => items.Select(item => new DesktopItemRow(item, item.IsMissing(), RemoveItemAsync)).ToList());

        CanvasItems.Clear();
        foreach (var row in rows)
        {
            CanvasItems.Add(row);
        }

        OnPropertyChanged(nameof(HasCanvasItems));

        // The dock's settings live in the same layout document, so they are read back with it.
        await ReloadDockAsync();
    }

    private void OnDesktopChanged(object? sender, DesktopModeStatus status)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyDesktopStatus(status);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => ApplyDesktopStatus(status));
        }
    }

    /// <summary>
    /// Shows where the desktop really is. The picker follows the mode that was asked for — which is what
    /// the service remembers, and what says whether the user's choice is showing — while the caption says
    /// what is actually happening, including the case where the takeover did not work and the canvas is
    /// showing over the icons instead.
    /// </summary>
    private void ApplyDesktopStatus(DesktopModeStatus status)
    {
        DesktopStateText = DescribeDesktopStatus(status);
        NeedsDesktopRecovery = status.NeedsRecovery;

        var index = Modes.ToList().FindIndex(row => row.Mode == status.Mode);
        if (index >= 0 && ModeIndex != index)
        {
            var wasApplying = _applyingMode;
            _applyingMode = true;
            try
            {
                ModeIndex = index;
            }
            finally
            {
                _applyingMode = wasApplying;
            }
        }

        if (status.IsShowingCanvas)
        {
            // A mount shows the layout as it is now, which may have been edited while the canvas was
            // off; the list follows the canvas rather than what the page last saw.
            _ = ReloadCanvasItemsAsync();
        }
    }

    /// <summary>
    /// Fills the mode picker, keeping whatever the user was on. The rows resolve their own labels, so a
    /// display-language change is a rebuild.
    /// </summary>
    private void ReloadModeRows()
    {
        var index = ModeIndex;

        var wasApplying = _applyingMode;
        _applyingMode = true;
        try
        {
            Modes.Clear();
            foreach (var mode in new[] { DesktopMode.Native, DesktopMode.Preview, DesktopMode.Takeover })
            {
                Modes.Add(new DesktopModeRow(mode, Loc));
            }

            ModeIndex = index >= 0 && index < Modes.Count ? index : 0;
        }
        finally
        {
            _applyingMode = wasApplying;
        }
    }

    private string DescribeDesktopStatus(DesktopModeStatus status)
    {
        if (status.NeedsRecovery)
        {
            return Loc.Format("Dynamic_Desktop_State_RecoveryRequired", status.Error ?? string.Empty);
        }

        var text = status.EffectiveMode switch
        {
            DesktopMode.Takeover => Loc.Format("Dynamic_Desktop_State_Takeover", _canvas.Status.ItemCount),
            DesktopMode.Preview => Loc.Format("Dynamic_Desktop_State_Preview", _canvas.Status.ItemCount),
            _ => Loc.Get("Dynamic_Desktop_State_Native"),
        };

        return status.HasError
            ? $"{text} {Loc.Format("Dynamic_Desktop_State_Problem", status.Error!)}"
            : text;
    }

    private void StartDiagnostics()
    {
        if (!IsDiagnosticsAvailable)
        {
            return;
        }

        if (_diagnosticsTimer is null)
        {
            _diagnosticsTimer = _dispatcherQueue.CreateTimer();
            _diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(250);
            _diagnosticsTimer.Tick += (_, _) => UpdateDiagnostics();
        }

        _diagnosticsTimer.Start();
        UpdateDiagnostics();
    }

    private void StopDiagnostics() => _diagnosticsTimer?.Stop();

    private void UpdateDiagnostics()
    {
        if (_canvas.Diagnostics is not { } snapshot)
        {
            DiagnosticsText = "canvas is not showing";
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"surface  {snapshot.SurfaceState ?? "?"} · mount {snapshot.MountCount} · {_canvas.Status.State}");
        report.AppendLine($"monitor  {snapshot.MonitorId ?? "?"} · {snapshot.BoundsPixels.Width}x{snapshot.BoundsPixels.Height} at {snapshot.BoundsPixels.X},{snapshot.BoundsPixels.Y} · {snapshot.Dpi} dpi ({snapshot.ScaleFactor:0.##}x)");
        report.AppendLine($"canvas   {snapshot.BoundsWidthDip:0.#} x {snapshot.BoundsHeightDip:0.#} DIP");
        report.AppendLine($"pointer  {(snapshot.PointerInside ? "inside " : "outside")} {snapshot.PointerXDip:0.#}, {snapshot.PointerYDip:0.#} DIP");
        report.AppendLine($"items    {snapshot.ItemCount} · hovered {snapshot.HoveredItemId ?? "none"} at {snapshot.HoveredScale:0.00}x");
        report.AppendLine($"missing  {snapshot.MissingItemIds?.Count ?? 0} · selected {snapshot.SelectedItemId ?? "none"}");
        report.AppendLine($"launch   {snapshot.LastLaunchId ?? "none"} → {snapshot.LastLaunchOutcome ?? "never"}");
        report.AppendLine($"icons    {snapshot.IconCacheEntries} cached · {snapshot.IconCacheBytes / (1024.0 * 1024.0):0.0} MB");
        report.AppendLine($"dock     {snapshot.DockPhase} · {snapshot.DockItemCount} items · {(snapshot.DockEnabled ? "on" : "off")} · {snapshot.DockEdge ?? "?"} edge · reveal {snapshot.DockScale:0.#}");
        report.AppendLine($"router   {snapshot.PointerContext ?? "?"} · {snapshot.PointerDispatchesPerSecond:0.0}/s · {snapshot.PointerReports} reports · {snapshot.PointerDispatches} dispatches");
        report.AppendLine($"updates  {snapshot.UpdatesPerSecond:0.0}/s · {snapshot.Updates} total");
        report.AppendLine($"layout   {snapshot.LayoutPath}");
        DiagnosticsText = report.ToString().TrimEnd();
    }

    private void OnStatusChanged(object? sender, VideoWallpaperStatus status)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyStatus(status);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        }
    }

    private void ApplyStatus(VideoWallpaperStatus status)
    {
        _state = status.State;
        StateText = DescribeState(status);

        if (status.State is VideoWallpaperState.Playing or VideoWallpaperState.Stopped)
        {
            ClearMessage();
        }

        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanStop));
    }

    private string DescribeState(VideoWallpaperStatus status) => status.State switch
    {
        VideoWallpaperState.Starting => Loc.Get("Dynamic_State_Starting"),
        VideoWallpaperState.Playing => Loc.Get("Dynamic_State_Playing"),
        VideoWallpaperState.Failed => Loc.Format("Dynamic_State_Failed", status.Error ?? string.Empty),
        _ => Loc.Get("Dynamic_State_Stopped"),
    };

    /// <summary>Sets the flag without going through the user-facing change handler.</summary>
    private void ApplyStartWithApp(bool value)
    {
        if (StartWithApp == value)
        {
            return;
        }

        var wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            StartWithApp = value;
        }
        finally
        {
            _applyingSettings = wasApplying;
        }
    }

    private void SetMessage(string key, bool isError, params object?[] args)
    {
        _message = (key, args);
        StatusMessage = Loc.Format(key, args);
        StatusIsError = isError;
        OnPropertyChanged(nameof(SuccessMessage));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private void ClearMessage()
    {
        if (_message is null && StatusMessage is null)
        {
            return;
        }

        _message = null;
        StatusMessage = null;
        OnPropertyChanged(nameof(SuccessMessage));
        OnPropertyChanged(nameof(ErrorMessage));
    }
}
