using System.Collections.ObjectModel;
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
/// Drives the dynamic wallpaper page: pick a video, put it on the desktop behind the icons, take it off
/// again, decide whether it comes back on the next launch, and keep the list of items the desktop canvas
/// holds. What the desktop itself is — the native one or Muralis Mode — is the Settings page's, not
/// this page's.
/// </summary>
public sealed partial class DynamicWallpaperViewModel : ViewModelBase
{
    private readonly IVideoWallpaperService _videoWallpaper;
    private readonly IDesktopCanvasService _canvas;
    private readonly IDesktopItemSyncService _desktopSync;
    private readonly ISettingsService _settingsService;
    private readonly IFilePickerService _filePicker;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger<DynamicWallpaperViewModel> _logger;
    private bool _applyingSettings = true;
    private VideoWallpaperState _state;
    private (string Key, object?[] Args)? _message;

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

    /// <summary>True while a refresh of the user's own desktop items is running.</summary>
    [ObservableProperty]
    public partial bool IsRefreshingItems { get; set; }

    /// <summary>What the user typed into the address box; cleared once the address is on the canvas.</summary>
    [ObservableProperty]
    public partial string NewItemUrl { get; set; }

    /// <summary>True while an import or a removal is in flight, so the buttons wait for it.</summary>
    [ObservableProperty]
    public partial bool IsCanvasItemsBusy { get; set; }

    public DynamicWallpaperViewModel(
        IVideoWallpaperService videoWallpaper,
        IDesktopCanvasService canvas,
        IDesktopItemSyncService desktopSync,
        ISettingsService settingsService,
        IFilePickerService filePicker,
        ILocalizationService localization,
        ILogger<DynamicWallpaperViewModel> logger)
        : base(localization)
    {
        _videoWallpaper = videoWallpaper;
        _canvas = canvas;
        _desktopSync = desktopSync;
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

    /// <summary>The items on the canvas, in the order the canvas keeps them.</summary>
    public ObservableCollection<DesktopItemRow> CanvasItems { get; }

    /// <summary>Whether there is anything to list yet; the empty hint shows until there is.</summary>
    public bool HasCanvasItems => CanvasItems.Count > 0;

    /// <summary>The import buttons wait while an import or a removal is running.</summary>
    public bool CanEditCanvasItems => !IsCanvasItemsBusy && !IsRefreshingItems;

    /// <summary>The refresh button waits while a refresh is running.</summary>
    public bool CanRefreshItems => !IsRefreshingItems && !IsCanvasItemsBusy;

    public override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(VideoFileName));
        StateText = DescribeState(_videoWallpaper.Status);

        if (_message is { } message)
        {
            StatusMessage = Loc.Format(message.Key, message.Args);
        }
    }

    public override void DetachFromPage()
    {
        _videoWallpaper.StatusChanged -= OnStatusChanged;
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
