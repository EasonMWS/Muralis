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
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

/// <summary>A rotation interval paired with its localized label.</summary>
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

/// <summary>A wallpaper fit mode paired with its localized label.</summary>
public sealed class FitModeOption
{
    public FitModeOption(WallpaperFitMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public WallpaperFitMode Mode { get; }

    public string Label { get; }
}

/// <summary>A rotation source (favorites or a folder) paired with its localized label.</summary>
public sealed class RotationSourceOption
{
    public RotationSourceOption(bool useFavorites, string label)
    {
        UseFavorites = useFavorites;
        Label = label;
    }

    public bool UseFavorites { get; }

    public string Label { get; }
}

public sealed class DesktopExperienceOption
{
    public DesktopExperienceOption(DesktopExperienceMode mode, string label, string description, bool isAvailable)
    {
        Mode = mode;
        Label = label;
        Description = description;
        IsAvailable = isAvailable;
    }

    public DesktopExperienceMode Mode { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsAvailable { get; }
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly RotationInterval[] IntervalOrder =
    [
        RotationInterval.Minutes15,
        RotationInterval.Minutes30,
        RotationInterval.Hours1,
        RotationInterval.Hours6,
        RotationInterval.Daily,
    ];

    private static readonly string[] IntervalKeys =
    [
        "Interval_15Minutes",
        "Interval_30Minutes",
        "Interval_1Hour",
        "Interval_6Hours",
        "Interval_Daily",
    ];

    private static readonly WallpaperFitMode[] FitModeOrder = Enum.GetValues<WallpaperFitMode>();

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly IWallpaperService _wallpaperService;
    private readonly IStartupService _startupService;
    private readonly RotationService _rotationService;
    private readonly WallpaperProviderManager _providers;
    private readonly IUpdateChecker _updateChecker;
    private readonly IDesktopExperienceService _desktopExperience;
    private readonly IDockExperienceService _dockExperience;
    private readonly WindowContext _windowContext;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _applyingSettings = true;
    private (string Key, object?[] Args)? _status;
    private (string Key, object?[] Args)? _updateStatus;
    private string _releaseUrl = AppInfo.GitHubReleasesUrl;

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

    /// <summary>
    /// The user's own answer to whether the dock is on the desktop. It is the saved preference rather
    /// than whether the window is up: while Clean Desktop runs, the dock is up whatever this says.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowDock { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; }

    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguageOption { get; set; }

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IFilePickerService filePicker,
        IDialogService dialogs,
        IWallpaperService wallpaperService,
        IStartupService startupService,
        RotationService rotationService,
        WallpaperProviderManager providers,
        IUpdateChecker updateChecker,
        IDesktopExperienceService desktopExperience,
        IDockExperienceService dockExperience,
        WindowContext windowContext,
        ILocalizationService localization,
        ILogger<SettingsViewModel> logger)
        : base(localization)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _wallpaperService = wallpaperService;
        _startupService = startupService;
        _rotationService = rotationService;
        _providers = providers;
        _updateChecker = updateChecker;
        _desktopExperience = desktopExperience;
        _dockExperience = dockExperience;
        _windowContext = windowContext;
        _logger = logger;

        _providers.Register(this);

        var settings = _settingsService.Current;
        CacheSizeText = Loc.Get("Settings_Status_Calculating");
        SetUpdateStatus("Settings_Update_Idle");
        DownloadFolder = ResolveDownloadFolder(settings);
        DefaultFitMode = settings.DefaultFitMode;
        LaunchAtStartup = SafeReadStartupState(settings.LaunchAtStartup);
        CloseToTray = settings.CloseToTray;
        RotationEnabled = settings.Rotation.Enabled;
        ShowDock = settings.Dock.IsVisible;

        FitModes = BuildFitModes();
        IntervalOptions = BuildIntervalOptions();
        RotationSourceOptions = BuildRotationSourceOptions();
        LanguageOptions = BuildLanguageOptions();
        DesktopExperienceOptions = BuildDesktopExperienceOptions();
        SelectedLanguageOption = FindLanguageOption(localization.Preference);
        BuildProviderSources();

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

    public IReadOnlyList<FitModeOption> FitModes { get; private set; } = [];

    public IReadOnlyList<IntervalOption> IntervalOptions { get; private set; } = [];

    public IReadOnlyList<RotationSourceOption> RotationSourceOptions { get; private set; } = [];

    public IReadOnlyList<LanguageOption> LanguageOptions { get; private set; } = [];

    public IReadOnlyList<DesktopExperienceOption> DesktopExperienceOptions { get; private set; } = [];

    public DesktopExperienceMode DesktopExperienceMode => _desktopExperience.Status.Mode;

    public string DesktopExperienceStateText => _desktopExperience.Status.IsCleanDesktopAvailable
        ? Loc.Get("Settings_DesktopExperience_StateReady")
        : Loc.Get("Settings_DesktopExperience_StatePhase4A");

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    /// <summary>Every registered source with its enable switch, in registration order.</summary>
    public ObservableCollection<ProviderSourceItem> ProviderSources { get; } = [];

    /// <summary>The usable sources the user can pick for the Home feed.</summary>
    public ObservableCollection<ProviderSourceItem> DefaultSourceItems { get; } = [];

    /// <summary>The source Home takes its featured wallpapers from.</summary>
    public ProviderSourceItem? SelectedDefaultSource
    {
        get => DefaultSourceItems.FirstOrDefault(item =>
            string.Equals(item.Id, _providers.DefaultProvider?.Id, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || _applyingSettings
                || string.Equals(value.Id, _providers.DefaultProvider?.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _providers.SetDefaultProvider(value.Id);
            OnPropertyChanged();
            SetStatus("Settings_Status_DefaultSourceUpdated", isError: false, value.Name);
        }
    }

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
                return Loc.Get("Settings_Rotation_StatusFavorites");
            }

            return string.IsNullOrWhiteSpace(rotation.SourceFolder)
                ? Loc.Get("Settings_Rotation_StatusChooseFolder")
                : rotation.SourceFolder;
        }
    }

    public string MonitorCountText => Monitors.Count switch
    {
        0 => Loc.Get("Settings_Displays_None"),
        1 => Loc.Get("Settings_Displays_One"),
        _ => Loc.Format("Settings_Displays_Many", Monitors.Count),
    };

    public string AppVersion => Loc.Format("Settings_About_Version", AppInfo.Version);

    public string GitHubUrl => AppInfo.GitHubUrl;

    /// <summary>Where "view release" points; already the releases page before any check ran.</summary>
    public string UpdateReleaseUrl => _releaseUrl;

    public override void OnLanguageChanged()
    {
        // Option labels and derived texts are rebuilt in place so combo selections survive.
        FitModes = BuildFitModes();
        IntervalOptions = BuildIntervalOptions();
        RotationSourceOptions = BuildRotationSourceOptions();
        LanguageOptions = BuildLanguageOptions();
        DesktopExperienceOptions = BuildDesktopExperienceOptions();

        OnPropertyChanged(nameof(FitModes));
        OnPropertyChanged(nameof(IntervalOptions));
        OnPropertyChanged(nameof(RotationSourceOptions));
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(DesktopExperienceOptions));
        OnPropertyChanged(nameof(DesktopExperienceStateText));
        OnPropertyChanged(nameof(RotationStatusText));
        OnPropertyChanged(nameof(MonitorCountText));
        OnPropertyChanged(nameof(AppVersion));
        OnPropertyChanged(nameof(SelectedDefaultSource));

        foreach (var item in ProviderSources)
        {
            item.Refresh();
        }

        if (_status is { } status)
        {
            var message = Loc.Format(status.Key, status.Args);
            StatusMessage = message;
            OnPropertyChanged(nameof(SuccessMessage));
            OnPropertyChanged(nameof(ErrorMessage));
        }

        if (_updateStatus is { } updateStatus)
        {
            UpdateStatusText = Loc.Format(updateStatus.Key, updateStatus.Args);
        }

        // Display names ("Display 1") are localized when monitors are enumerated.
        _ = RefreshMonitorsAsync();
    }

    /// <summary>Called by the settings page after it rebuilds its combo boxes.</summary>
    public void SelectLanguage(LanguageOption option)
    {
        if (option is null || option.Preference == Loc.Preference)
        {
            return;
        }

        Loc.Apply(option.Preference);
        _settingsService.Update(settings => settings.Language = option.Preference);
        SetStatus("Settings_Status_LanguageChanged", isError: false);
    }

    public void SetRotationInterval(RotationInterval interval)
    {
        if (interval == _settingsService.Current.Rotation.Interval)
        {
            return;
        }

        _settingsService.Update(settings => settings.Rotation.Interval = interval);
        SetStatus("Settings_Status_IntervalUpdated", isError: false);
    }

    public async Task SelectDesktopExperienceAsync(DesktopExperienceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var result = await _desktopExperience.ApplyAsync(option.Mode);
        OnPropertyChanged(nameof(DesktopExperienceMode));
        OnPropertyChanged(nameof(DesktopExperienceStateText));

        if (result.HasError)
        {
            SetStatus("Settings_Status_DesktopExperienceUnavailable", isError: true, result.Error ?? string.Empty);
            return;
        }

        SetStatus("Settings_Status_DesktopExperienceChanged", isError: false, option.Label);
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
            Loc.Get("Settings_ConfirmClearCache_Title"),
            Loc.Get("Settings_ConfirmClearCache_Message"),
            Loc.Get("Settings_ConfirmClearCache_Primary"),
            Loc.Get("Common_Cancel"));

        if (!confirmed)
        {
            return;
        }

        if (DirectoryHelper.TryClearDirectory(AppPaths.CacheDirectory, out var error))
        {
            RefreshCacheSize();
            SetStatus("Settings_Status_CacheCleared", isError: false, CacheSizeText);
        }
        else
        {
            _logger.LogWarning("Cache cleanup failed: {Error}", error);
            SetStatus("Settings_Status_CacheClearFailed", isError: true);
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
        SetStatus("Settings_Status_DownloadFolderUpdated", isError: false);
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
            SetStatus("Settings_Status_OpenFolderFailed", isError: true);
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
        SetStatus("Settings_Status_RotationFolderUpdated", isError: false);
    }

    [RelayCommand]
    private async Task ShuffleNowAsync()
    {
        SetStatus("Settings_Status_ShuffleApplying", isError: false);
        var applied = await _rotationService.ApplyNextAsync();
        if (applied is null)
        {
            SetStatus("Settings_Status_ShuffleNone", isError: true);
            return;
        }

        SetStatus("Settings_Status_Shuffled", isError: false, applied.Title);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        SetUpdateStatus("Settings_Update_Checking");

        try
        {
            var result = await _updateChecker.CheckAsync(AppInfo.Version);
            if (result is null)
            {
                UpdateAvailable = false;
                SetUpdateStatus("Settings_Update_None");
            }
            else if (result.IsUpdateAvailable)
            {
                UpdateAvailable = true;
                _releaseUrl = result.ReleaseUrl ?? AppInfo.GitHubReleasesUrl;
                OnPropertyChanged(nameof(UpdateReleaseUrl));
                SetUpdateStatus("Settings_Update_Available", result.LatestVersion);
            }
            else
            {
                UpdateAvailable = false;
                SetUpdateStatus("Settings_Update_UpToDate", result.LatestVersion);
            }
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            _logger.LogWarning(ex, "The update check failed");
            SetUpdateStatus("Settings_Update_Failed");
        }
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

    public string FitModeLabel(WallpaperFitMode mode) => Loc.Get(FitModeKeys.For(mode));

    partial void OnDefaultFitModeChanged(WallpaperFitMode value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.DefaultFitMode = value);
        SetStatus("Settings_Status_FitModeSet", isError: false, FitModeLabel(value));
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
            SetStatus(value ? "Settings_Status_StartupEnabled" : "Settings_Status_StartupDisabled", isError: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update the startup registration");
            SetStatus("Settings_Status_StartupFailed", isError: true);
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.CloseToTray = value);
        SetStatus(value ? "Settings_Status_TrayEnabled" : "Settings_Status_TrayDisabled", isError: false);
    }

    partial void OnRotationEnabledChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Update(settings => settings.Rotation.Enabled = value);
        SetStatus(value ? "Settings_Status_RotationEnabled" : "Settings_Status_RotationDisabled", isError: false);
    }

    partial void OnShowDockChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }

        _ = ApplyDockVisibilityAsync(value);
    }

    /// <summary>
    /// Shows or hides the dock and says where it ended up. The service owns the setting and the
    /// window, so this only reports what it answered.
    /// </summary>
    private async Task ApplyDockVisibilityAsync(bool visible)
    {
        try
        {
            if (!await _dockExperience.SetVisibleAsync(visible))
            {
                SetStatus("Settings_Status_DockFailed", isError: true);
                return;
            }

            SetStatus(visible ? "Settings_Status_DockShown" : "Settings_Status_DockHidden", isError: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The dock's visibility could not be changed");
            SetStatus("Settings_Status_DockFailed", isError: true);
        }
    }

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        if (_applyingSettings || value is null)
        {
            return;
        }

        SelectLanguage(value);
    }

    private IReadOnlyList<FitModeOption> BuildFitModes() =>
        FitModeOrder.Select(mode => new FitModeOption(mode, Loc.Get(FitModeKeys.For(mode)))).ToList();

    private IReadOnlyList<IntervalOption> BuildIntervalOptions() =>
        IntervalOrder
            .Select((interval, index) => new IntervalOption(interval, Loc.Get(IntervalKeys[index])))
            .ToList();

    private IReadOnlyList<RotationSourceOption> BuildRotationSourceOptions() =>
    [
        new RotationSourceOption(true, Loc.Get("Settings_Rotation_SourceFavorites")),
        new RotationSourceOption(false, Loc.Get("Settings_Rotation_SourceFolder")),
    ];

    private IReadOnlyList<LanguageOption> BuildLanguageOptions() => Loc.Languages;

    private IReadOnlyList<DesktopExperienceOption> BuildDesktopExperienceOptions() =>
    [
        new(
            DesktopExperienceMode.Native,
            Loc.Get("Settings_DesktopExperience_Native"),
            Loc.Get("Settings_DesktopExperience_Native_Description"),
            true),
        new(
            DesktopExperienceMode.CleanDesktop,
            Loc.Get("Settings_DesktopExperience_Clean"),
            Loc.Get("Settings_DesktopExperience_Clean_Description"),
            _desktopExperience.Status.IsCleanDesktopAvailable),
        new(
            DesktopExperienceMode.FullTakeoverExperimental,
            Loc.Get("Settings_DesktopExperience_Full"),
            Loc.Get("Settings_DesktopExperience_Full_Description"),
            true),
    ];

    private void BuildProviderSources()
    {
        ProviderSources.Clear();
        foreach (var provider in _providers.Providers)
        {
            ProviderSources.Add(new ProviderSourceItem(provider, _providers, Loc, ReportProviderStatus));
        }

        RebuildDefaultSourceItems();
    }

    /// <summary>
    /// Rebuilds only the default-source picker: enable switches re-filter it, while the
    /// row list itself stays untouched so no visual tree is torn down mid-toggle.
    /// </summary>
    private void RebuildDefaultSourceItems()
    {
        DefaultSourceItems.Clear();
        foreach (var item in ProviderSources.Where(item => item.Availability == ProviderAvailability.Available))
        {
            DefaultSourceItems.Add(item);
        }

        OnPropertyChanged(nameof(SelectedDefaultSource));
    }

    private void ReportProviderStatus(string key, string providerName, bool isError) =>
        SetStatus(key, isError, providerName);

    public override void OnProvidersChanged()
    {
        foreach (var item in ProviderSources)
        {
            item.Refresh();
        }

        RebuildDefaultSourceItems();
    }

    private LanguageOption FindLanguageOption(string preference) =>
        LanguageOptions.FirstOrDefault(option => option.Preference == preference) ?? LanguageOptions[0];

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

    private void SetStatus(string key, bool isError, params object?[] args)
    {
        _status = (key, args);
        StatusMessage = Loc.Format(key, args);
        StatusIsError = isError;
        OnPropertyChanged(nameof(SuccessMessage));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatus = (key, args);
        UpdateStatusText = Loc.Format(key, args);
    }
}
