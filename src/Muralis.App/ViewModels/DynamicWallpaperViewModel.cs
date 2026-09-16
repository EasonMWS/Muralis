using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>
/// Drives the dynamic wallpaper page: pick a video, put it on the desktop behind the icons,
/// take it off again, decide whether it comes back on the next launch, and look after the
/// experimental desktop canvas prototype — switch it on or off, add the programs, shortcuts,
/// folders and addresses that should live on it, and take them off again.
/// </summary>
public sealed partial class DynamicWallpaperViewModel : ViewModelBase
{
    private readonly IVideoWallpaperService _videoWallpaper;
    private readonly IDesktopCanvasService _canvas;
    private readonly ISettingsService _settingsService;
    private readonly IFilePickerService _filePicker;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger<DynamicWallpaperViewModel> _logger;
    private bool _applyingSettings = true;
    private bool _applyingCanvasStatus;
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

    /// <summary>Whether the experimental desktop canvas is on the desktop.</summary>
    [ObservableProperty]
    public partial bool CanvasEnabled { get; set; }

    /// <summary>Live state of the desktop canvas, shown as the canvas card's caption.</summary>
    [ObservableProperty]
    public partial string CanvasStateText { get; set; }

    [ObservableProperty]
    public partial bool IsCanvasBusy { get; set; }

    /// <summary>What the user typed into the address box; cleared once the address is on the canvas.</summary>
    [ObservableProperty]
    public partial string NewItemUrl { get; set; }

    /// <summary>True while an import or a removal is in flight, so the buttons wait for it.</summary>
    [ObservableProperty]
    public partial bool IsCanvasItemsBusy { get; set; }

    /// <summary>Whether the development diagnostics panel is expanded.</summary>
    [ObservableProperty]
    public partial bool IsDiagnosticsOpen { get; set; }

    /// <summary>The latest canvas measurements, one field per line; development builds only.</summary>
    [ObservableProperty]
    public partial string DiagnosticsText { get; set; }

    public DynamicWallpaperViewModel(
        IVideoWallpaperService videoWallpaper,
        IDesktopCanvasService canvas,
        ISettingsService settingsService,
        IFilePickerService filePicker,
        ILocalizationService localization,
        ILogger<DynamicWallpaperViewModel> logger)
        : base(localization)
    {
        _videoWallpaper = videoWallpaper;
        _canvas = canvas;
        _settingsService = settingsService;
        _filePicker = filePicker;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        CanvasItems = [];
        NewItemUrl = string.Empty;

        var saved = settingsService.Current.VideoWallpaper;
        VideoPath = saved.VideoPath;
        Muted = saved.Muted;
        StartWithApp = saved.Enabled;
        StateText = string.Empty;

        _videoWallpaper.StatusChanged += OnStatusChanged;
        ApplyStatus(_videoWallpaper.Status);

        // The toggle starts where the service already is — the app may have restored the canvas
        // before this page was ever opened.
        _canvas.StatusChanged += OnCanvasStatusChanged;
        CanvasStateText = DescribeCanvasStatus(_canvas.Status);
        CanvasEnabled = _canvas.Status.State == CanvasPrototypeState.Active;
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

    public bool CanToggleCanvas => !IsCanvasBusy;

    /// <summary>The items on the canvas, in the order the canvas keeps them.</summary>
    public ObservableCollection<DesktopItemRow> CanvasItems { get; }

    /// <summary>Whether there is anything to list yet; the empty hint shows until there is.</summary>
    public bool HasCanvasItems => CanvasItems.Count > 0;

    /// <summary>The import buttons wait while an import or a removal is running.</summary>
    public bool CanEditCanvasItems => !IsCanvasItemsBusy;

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
        CanvasStateText = DescribeCanvasStatus(_canvas.Status);

        if (_message is { } message)
        {
            StatusMessage = Loc.Format(message.Key, message.Args);
        }
    }

    public override void DetachFromPage()
    {
        _videoWallpaper.StatusChanged -= OnStatusChanged;
        _canvas.StatusChanged -= OnCanvasStatusChanged;
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

    partial void OnCanvasEnabledChanged(bool value)
    {
        if (_applyingSettings || _applyingCanvasStatus)
        {
            return;
        }

        _ = ApplyCanvasAsync(value);
    }

    partial void OnIsCanvasBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggleCanvas));

    partial void OnIsCanvasItemsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditCanvasItems));

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
    /// Shows or removes the canvas. The enabled flag is only remembered once the canvas is really
    /// on the desktop, so a failed attempt is not restored — and reported — on every launch.
    /// </summary>
    private async Task ApplyCanvasAsync(bool enabled)
    {
        IsCanvasBusy = true;
        try
        {
            CanvasPrototypeStatus status;
            if (enabled)
            {
                status = await _canvas.EnableAsync();
            }
            else
            {
                await _canvas.DisableAsync();
                status = _canvas.Status;
            }

            _settingsService.Update(settings => settings.DesktopCanvas.Enabled = status.State == CanvasPrototypeState.Active);

            if (status.State == CanvasPrototypeState.Failed)
            {
                SetMessage("Dynamic_Canvas_Status_Failed", isError: true, status.Error ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop canvas could not be switched");
            _settingsService.Update(settings => settings.DesktopCanvas.Enabled = false);
            SetMessage("Dynamic_Canvas_Status_Failed", isError: true, ex.Message);
        }
        finally
        {
            IsCanvasBusy = false;
            ApplyCanvasStatus(_canvas.Status);
        }
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
    }

    private void OnCanvasStatusChanged(object? sender, CanvasPrototypeStatus status)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyCanvasStatus(status);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => ApplyCanvasStatus(status));
        }
    }

    private void ApplyCanvasStatus(CanvasPrototypeStatus status)
    {
        CanvasStateText = DescribeCanvasStatus(status);

        // Keep the switch in step with reality: a failed mount flips it back off, a restored
        // canvas flips it on without going through the change handler.
        if (CanvasEnabled != (status.State == CanvasPrototypeState.Active))
        {
            var wasApplying = _applyingCanvasStatus;
            _applyingCanvasStatus = true;
            try
            {
                CanvasEnabled = status.State == CanvasPrototypeState.Active;
            }
            finally
            {
                _applyingCanvasStatus = wasApplying;
            }
        }

        if (status.State == CanvasPrototypeState.Active)
        {
            // A mount shows the layout as it is now, which may have been edited while the canvas was
            // off; the list follows the canvas rather than what the page last saw.
            _ = ReloadCanvasItemsAsync();
        }
    }

    private string DescribeCanvasStatus(CanvasPrototypeStatus status) => status.State switch
    {
        CanvasPrototypeState.Starting => Loc.Get("Dynamic_Canvas_State_Starting"),
        CanvasPrototypeState.Active => Loc.Format("Dynamic_Canvas_State_Active", status.ItemCount),
        CanvasPrototypeState.Failed => Loc.Format("Dynamic_Canvas_State_Failed", status.Error ?? string.Empty),
        _ => Loc.Get("Dynamic_Canvas_State_Disabled"),
    };

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
        report.AppendLine($"dock     {snapshot.DockPhase} at {snapshot.DockScale:0.00}x");
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
