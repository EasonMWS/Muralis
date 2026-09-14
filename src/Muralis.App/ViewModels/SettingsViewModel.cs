using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed class IntervalOption
{
    public IntervalOption(RotationInterval value, string label)
    {
        Value = value;
        Label = label;
    }

    public RotationInterval Value { get; }

    public string Label { get; }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly IWallpaperService _wallpaperService;
    private readonly IStartupService _startupService;
    private readonly RotationService _rotationService;
    private readonly WindowContext _windowContext;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _applyingSettings = true;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial string CacheSizeText { get; set; }

    [ObservableProperty]
    public partial string DownloadFolder { get; set; }

    [ObservableProperty]
    public partial WallpaperFitMode DefaultFitMode { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool RotationEnabled { get; set; }

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IFilePickerService filePicker,
        IDialogService dialogs,
        IWallpaperService wallpaperService,
        IStartupService startupService,
        RotationService rotationService,
        WindowContext windowContext,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _wallpaperService = wallpaperService;
        _startupService = startupService;
        _rotationService = rotationService;
        _windowContext = windowContext;
        _logger = logger;

        var settings = _settingsService.Current;
        CacheSizeText = "Calculating…";
        DownloadFolder = ResolveDownloadFolder(settings);
        DefaultFitMode = settings.DefaultFitMode;
        LaunchAtStartup = SafeReadStartupState(settings.LaunchAtStartup);
        CloseToTray = settings.CloseToTray;
        RotationEnabled = settings.Rotation.Enabled;

        _applyingSettings = false;
    }

    public string? SuccessMessage => StatusIsError ? null : StatusMessage;

    public string? ErrorMessage => StatusIsError ? StatusMessage : null;

    public bool IsSystemTheme
    {
        get => _settingsService.Current.Theme == AppTheme.System;
        set
        {
            if (value)
            {
                SelectTheme(AppTheme.System);
            }
        }
    }

    public bool IsLightTheme
    {
        get => _settingsService.Current.Theme == AppTheme.Light;
        set
        {
            if (value)
            {
                SelectTheme(AppTheme.Light);
            }
        }
    }

    public bool IsDarkTheme
    {
        get => _settingsService.Current.Theme == AppTheme.Dark;
        set
        {
            if (value)
            {
                SelectTheme(AppTheme.Dark);
            }
        }
    }

    public IReadOnlyList<WallpaperFitMode> FitModes { get; } = Enum.GetValues<WallpaperFitMode>();

    public IReadOnlyList<IntervalOption> IntervalOptions { get; } =
    [
        new IntervalOption(RotationInterval.Minutes15, "Every 15 minutes"),
        new IntervalOption(RotationInterval.Minutes30, "Every 30 minutes"),
        new IntervalOption(RotationInterval.Hours1, "Every hour"),
        new IntervalOption(RotationInterval.Hours6, "Every 6 hours"),
        new IntervalOption(RotationInterval.Daily, "Every day"),
    ];

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public IntervalOption CurrentIntervalOption =>
        IntervalOptions.FirstOrDefault(option => option.Value == _settingsService.Current.Rotation.Interval)
        ?? IntervalOptions[1];

    public bool UseFavoritesSource
    {
        get => _settingsService.Current.Rotation.UseFavorites;
        set
        {
            if (value == _settingsService.Current.Rotation.UseFavorites)
            {
                return;
            }

            _settingsService.Update(settings => settings.Rotation.UseFavorites = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RotationStatusText));
        }
    }

    public string RotationStatusText
    {
        get
        {
            var rotation = _settingsService.Current.Rotation;
            if (rotation.UseFavorites)
            {
                return "Uses your favorites as the source.";
            }

            return string.IsNullOrWhiteSpace(rotation.SourceFolder)
                ? "Choose a folder to rotate through."
                : rotation.SourceFolder;
        }
    }

    public string MonitorCountText => Monitors.Count switch
    {
        0 => "No displays detected",
        1 => "1 display detected",
        _ => $"{Monitors.Count} displays detected",
    };

    public string AppVersion => $"Version {AppInfo.Version}";

    public string GitHubUrl => AppInfo.GitHubUrl;

    public void SetRotationInterval(RotationInterval interval)
    {
        if (interval == _settingsService.Current.Rotation.Interval)
        {
            return;
        }

        _settingsService.Update(settings => settings.Rotation.Interval = interval);
        SetStatus("Rotation interval updated.", isError: false);
    }

    [RelayCommand]
    private async Task RefreshMonitorsAsync()
    {
        try
        {
            var monitors = await _wallpaperService.GetMonitorsAsync();
            Monitors.Clear();
            foreach (var monitor in monitors)
            {
                Monitors.Add(monitor);
            }

            OnPropertyChanged(nameof(MonitorCountText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate displays");
        }
    }

    [RelayCommand]
    private void RefreshCacheSize()
    {
        var bytes = DirectoryHelper.GetDirectorySize(AppPaths.CacheDirectory);
        CacheSizeText = bytes > 0 ? DisplayFormat.FileSize(bytes) : "0 B";
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var confirmed = await _dialogs.ShowConfirmAsync(
            "Clear cache",
            "Cached thumbnails and temporary files will be removed. Your wallpapers and settings are not affected.",
            "Clear cache");

        if (!confirmed)
        {
            return;
        }

        if (DirectoryHelper.TryClearDirectory(AppPaths.CacheDirectory, out var error))
        {
            RefreshCacheSize();
            SetStatus($"Cache cleared. Current size: {CacheSizeText}.", isError: false);
        }
        else
        {
            _logger.LogWarning("Cache cleanup failed: {Error}", error);
            SetStatus("Some cache files could not be removed. They may be in use.", isError: true);
        }
    }

    [RelayCommand]
    private async Task ChangeDownloadFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        _settingsService.Update(settings => settings.DownloadFolder = folder);
        DownloadFolder = folder;
        SetStatus("Download folder updated.", isError: false);
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        var folder = DownloadFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Folder}", folder);
            SetStatus("Could not open the download folder.", isError: true);
        }
    }

    [RelayCommand]
    private async Task ChooseRotationFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        _settingsService.Update(settings =>
        {
            settings.Rotation.SourceFolder = folder;
            settings.Rotation.UseFavorites = false;
        });

        OnPropertyChanged(nameof(UseFavoritesSource));
        OnPropertyChanged(nameof(RotationStatusText));
        SetStatus("Rotation folder updated.", isError: false);
    }

    [RelayCommand]
    private async Task ShuffleNowAsync()
    {
        SetStatus("Applying the next wallpaper…", isError: false);
        var applied = await _rotationService.ApplyNextAsync();
        if (applied is null)
        {
            SetStatus("No wallpapers available to rotate through yet.", isError: true);
            return;
        }

        SetStatus($"Wallpaper changed to \"{applied.Title}\".", isError: false);
    }

    [RelayCommand]
    private void ExitApplication() => (_windowContext.MainWindow as MainWindow)?.ExitApplication();

    public void SelectTheme(AppTheme theme)
    {
        _settingsService.Update(settings => settings.Theme = theme);
        _themeService.Apply(theme);
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    partial void OnDefaultFitModeChanged(WallpaperFitMode value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.DefaultFitMode = value);
        SetStatus($"Default wallpaper style set to {value}.", isError: false);
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        try
        {
            _startupService.SetEnabled(value);
            _settingsService.Update(settings => settings.LaunchAtStartup = value);
            SetStatus(value ? "Muralis will start with Windows." : "Start with Windows disabled.", isError: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update the startup registration");
            SetStatus("Could not change the startup setting.", isError: true);
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.CloseToTray = value);
        SetStatus(
            value
                ? "Muralis keeps running in the tray when the window is closed."
                : "Muralis exits when the window is closed.",
            isError: false);
    }

    partial void OnRotationEnabledChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.Rotation.Enabled = value);
        SetStatus(value ? "Auto rotation enabled." : "Auto rotation disabled.", isError: false);
    }

    private bool SafeReadStartupState(bool fallback)
    {
        try
        {
            return _startupService.IsEnabled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the startup registration");
            return fallback;
        }
    }

    private static string ResolveDownloadFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloadFolder) ? AppPaths.DefaultDownloadFolder : settings.DownloadFolder;

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
        OnPropertyChanged(nameof(SuccessMessage));
        OnPropertyChanged(nameof(ErrorMessage));
    }
}
