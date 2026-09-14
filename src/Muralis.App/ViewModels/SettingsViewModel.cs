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

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
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

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IFilePickerService filePicker,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _logger = logger;

        var settings = _settingsService.Current;
        CacheSizeText = "Calculating…";
        DownloadFolder = ResolveDownloadFolder(settings);
        DefaultFitMode = settings.DefaultFitMode;
        LaunchAtStartup = settings.LaunchAtStartup;

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

    public bool IsStartupSupported => false;

    public bool IsRotationSupported => false;

    public string AppVersion => $"Version {AppInfo.Version}";

    public string GitHubUrl => AppInfo.GitHubUrl;

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

        _settingsService.Update(settings => settings.LaunchAtStartup = value);
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
