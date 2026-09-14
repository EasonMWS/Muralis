using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

public sealed partial class BrowseViewModel : ViewModelBase
{
    private readonly WallpaperProviderManager _providers;
    private readonly IImageCacheService _imageCache;
    private readonly INavigationService _navigation;
    private readonly ILogger<BrowseViewModel> _logger;
    private CancellationTokenSource? _searchDebounce;
    private readonly bool _isInitialized;
    private bool _suppressReload;
    private string? _errorKey;
    private object?[] _errorArgs = [];
    private bool _errorIsWarning;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial SourceOption? SelectedSource { get; set; }

    public BrowseViewModel(
        WallpaperProviderManager providers,
        INavigationService navigation,
        IImageCacheService imageCache,
        ILocalizationService localization,
        ILogger<BrowseViewModel> logger)
        : base(localization)
    {
        _providers = providers;
        _navigation = navigation;
        _imageCache = imageCache;
        _logger = logger;

        providers.Register(this);
        RebuildSources(selectDefault: true);

        SearchText = string.Empty;
        _isInitialized = true;
    }

    /// <summary>The "all sources" entry followed by every usable provider.</summary>
    public ObservableCollection<SourceOption> Sources { get; } = [];

    public ObservableCollection<Wallpaper> Items { get; } = [];

    public bool HasAvailableSources => _providers.EnabledProviders.Count > 0;

    public string ProviderName => SelectedSource?.Name ?? Loc.Get("Browse_ProviderFallback");

    public bool SupportsSearch => SelectedSource?.Provider is { } provider
        ? provider.SupportsSearch
        : _providers.EnabledProviders.Any(candidate => candidate.SupportsSearch);

    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0;

    public string ResultSummary => Items.Count == 1
        ? Loc.Get("Browse_Result_One")
        : Loc.Format("Browse_Result_Many", Items.Count);

    public bool IsInitialLoading => IsLoading && Items.Count == 0;

    public InfoBarSeverity ErrorSeverity => _errorIsWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Error;

    public string EmptyTitle => HasAvailableSources ? Loc.Get("Browse_Empty_Title") : Loc.Get("Browse_NoSources_Title");

    public string EmptyDescription => HasAvailableSources ? Loc.Get("Browse_Empty_Description") : Loc.Get("Browse_NoSources_Description");

    public string EmptyActionText => HasAvailableSources ? Loc.Get("Browse_Empty_Action") : Loc.Get("Browse_GotoSettings");

    public override void OnLanguageChanged()
    {
        foreach (var source in Sources)
        {
            source.RefreshName();
        }

        if (_errorKey is not null)
        {
            ErrorMessage = Loc.Format(_errorKey, _errorArgs);
        }

        NotifyStateChanged();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(EmptyActionText));
    }

    public override void OnProvidersChanged()
    {
        RebuildSources(selectDefault: false);
        NotifyStateChanged();
    }

    /// <summary>Primary action of the empty state: clear the search, or open the settings when no source is usable.</summary>
    [RelayCommand]
    private void EmptyAction()
    {
        if (!HasAvailableSources)
        {
            _navigation.NavigateTo(Routes.Settings);
            return;
        }

        SearchText = string.Empty;
    }

    [RelayCommand]
    private Task LoadAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void OpenWallpaper(Wallpaper? wallpaper)
    {
        if (wallpaper is not null)
        {
            _navigation.NavigateTo(Routes.Detail, wallpaper);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_isInitialized)
        {
            _ = DebouncedSearchAsync();
        }
    }

    partial void OnSelectedSourceChanged(SourceOption? value)
    {
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(SupportsSearch));

        if (_isInitialized && !_suppressReload)
        {
            _ = LoadCoreAsync(CancellationToken.None);
        }
    }

    private void RebuildSources(bool selectDefault)
    {
        var previousId = SelectedSource?.Provider?.Id;
        var wasAllSources = SelectedSource?.IsAllSources ?? false;

        Sources.Clear();
        Sources.Add(new SourceOption(null, Loc));
        foreach (var provider in _providers.EnabledProviders)
        {
            Sources.Add(new SourceOption(provider, Loc));
        }

        var restored = Sources.FirstOrDefault(source =>
                wasAllSources && source.IsAllSources
                || (!wasAllSources && source.Provider is not null && string.Equals(source.Provider.Id, previousId, StringComparison.OrdinalIgnoreCase)))
            ?? (selectDefault
                ? Sources.FirstOrDefault(source => string.Equals(source.Provider?.Id, _providers.DefaultProvider?.Id, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? Sources[0];

        _suppressReload = true;
        try
        {
            SelectedSource = restored;
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private async Task DebouncedSearchAsync()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;

        try
        {
            await Task.Delay(300, cts.Token).ConfigureAwait(true);
            await LoadCoreAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        finally
        {
            if (ReferenceEquals(_searchDebounce, cts))
            {
                _searchDebounce = null;
            }

            cts.Dispose();
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        var scope = SelectedSource;
        IsLoading = true;
        SetError(null);
        NotifyStateChanged();

        try
        {
            var result = await _providers
                .SearchAsync(
                    new WallpaperQuery
                    {
                        SearchText = SupportsSearch ? SearchText : null,
                        PageSize = 60,
                    },
                    scope?.Provider?.Id,
                    cancellationToken)
                .ConfigureAwait(true);

            Items.Clear();
            foreach (var item in result.Items)
            {
                Items.Add(item);
            }

            ReportFailures(result.Failures);
            _logger.LogInformation(
                "Browse loaded {Count} wallpapers from '{Scope}'",
                Items.Count,
                scope?.Provider?.Id ?? "all sources");

            // Thumbnails are cached in the background; cards update as files arrive.
            _ = _imageCache.WarmThumbnailsAsync(Items.ToList(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded or the page was left.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load wallpapers for '{Scope}'", scope?.Provider?.Id ?? "all sources");
            SetError("Browse_Error_LoadFailed");
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    private void ReportFailures(IReadOnlyList<ProviderFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (Items.Count == 0)
        {
            SetError("Browse_Error_LoadFailed");
            return;
        }

        var names = string.Join(
            Loc.Get("Common_ListSeparator"),
            failures.Select(failure => ProviderDisplay.Name(Loc, failure.Provider)));

        SetError("Browse_Error_SomeSourcesUnavailable", isWarning: true, names);
    }

    /// <summary>Stores the resource key so the message can be re-resolved after a language change.</summary>
    private void SetError(string? key, bool isWarning = false, params object?[] args)
    {
        _errorKey = key;
        _errorArgs = args;
        _errorIsWarning = isWarning;
        ErrorMessage = key is null ? null : Loc.Format(key, args);
        OnPropertyChanged(nameof(ErrorSeverity));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(HasAvailableSources));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(SupportsSearch));
    }
}
