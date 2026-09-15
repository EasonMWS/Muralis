using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>
/// Drives the dynamic wallpaper page: pick a video, put it on the desktop behind the icons,
/// take it off again, and decide whether it comes back on the next launch.
/// </summary>
public sealed partial class DynamicWallpaperViewModel : ViewModelBase
{
    private readonly IVideoWallpaperService _videoWallpaper;
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

    public DynamicWallpaperViewModel(
        IVideoWallpaperService videoWallpaper,
        ISettingsService settingsService,
        IFilePickerService filePicker,
        ILocalizationService localization,
        ILogger<DynamicWallpaperViewModel> logger)
        : base(localization)
    {
        _videoWallpaper = videoWallpaper;
        _settingsService = settingsService;
        _filePicker = filePicker;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var saved = settingsService.Current.VideoWallpaper;
        VideoPath = saved.VideoPath;
        Muted = saved.Muted;
        StartWithApp = saved.Enabled;
        StateText = string.Empty;
        _applyingSettings = false;

        _videoWallpaper.StatusChanged += OnStatusChanged;
        ApplyStatus(_videoWallpaper.Status);
    }

    public string? SuccessMessage => StatusIsError ? null : StatusMessage;

    public string? ErrorMessage => StatusIsError ? StatusMessage : null;

    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoPath);

    public string VideoFileName => HasVideo ? Path.GetFileName(VideoPath) : Loc.Get("Dynamic_NoVideo");

    public bool IsPlaying => _state == VideoWallpaperState.Playing;

    public bool CanPlay => HasVideo && !IsBusy && _state is VideoWallpaperState.Stopped or VideoWallpaperState.Failed;

    public bool CanStop => !IsBusy && _state is VideoWallpaperState.Starting or VideoWallpaperState.Playing;

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
