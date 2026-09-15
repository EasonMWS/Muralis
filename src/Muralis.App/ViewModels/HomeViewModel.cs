using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly WallpaperProviderManager _providers;
    private readonly IImageCacheService _imageCache;
    private readonly ILocalLibrary _library;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private string? _errorKey;
    private object?[] _errorArgs = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial Wallpaper? Featured { get; set; }

    public HomeViewModel(
        WallpaperProviderManager providers,
        IImageCacheService imageCache,
        ILocalLibrary library,
        INavigationService navigation,
        ILocalizationService localization,
        ILogger<HomeViewModel> logger)
        : base(localization)
    {
        _providers = providers;
        _imageCache = imageCache;
        _library = library;
        _navigation = navigation;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _library.Changed += OnLibraryChanged;
    }

    public ObservableCollection<Wallpaper> Recommended { get; } = [];

    public ObservableCollection<Wallpaper> RecentlyUsed { get; } = [];

    public bool HasRecent => RecentlyUsed.Count > 0;

    public string Greeting => Loc.Get(DateTime.Now.Hour switch
    {
        < 5 => "Greeting_Night",
        < 12 => "Greeting_Morning",
        < 18 => "Greeting_Afternoon",
        _ => "Greeting_Evening",
    });

    public bool IsInitialLoading => IsLoading && Recommended.Count == 0;

    public override void OnLanguageChanged()
    {
        if (_errorKey is not null)
        {
            ErrorMessage = Loc.Format(_errorKey, _errorArgs);
        }

        OnPropertyChanged(nameof(Greeting));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsInitialLoading));

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        SetError(null);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, PageToken);
        var linkedToken = linkedCancellation.Token;

        try
        {
            var result = await _providers
                .GetFeaturedAsync(24, linkedToken)
                .ConfigureAwait(true);

            if (result.Items.Count == 0)
            {
                _logger.LogWarning("No source could provide a featured feed");
                SetError("Home_Error_LoadFailed");
            }
            else if (result.Failures.Count > 0)
            {
                // The default source failed and the samples filled in.
                SetError("Home_Error_SourceUnavailable", ProviderDisplay.Name(Loc, result.Failures[0].Provider));
            }

            Recommended.Clear();
            foreach (var item in result.Items)
            {
                Recommended.Add(item);
            }

            Featured = Recommended.FirstOrDefault();
            await RefreshRecentAsync();

            _logger.LogInformation("Home feed loaded with {Count} wallpapers", Recommended.Count);

            // Pull thumbnails in the background so the hero and cards fill in as files arrive.
            // The page token stops that work when the user navigates away.
            _ = _imageCache.WarmThumbnailsAsync(Recommended.ToList(), PageToken);
        }
        catch (OperationCanceledException)
        {
            // The page was left while loading; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the home feed");
            SetError("Home_Error_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenWallpaper(Wallpaper? wallpaper)
    {
        if (wallpaper is not null)
        {
            _navigation.NavigateTo(Routes.Detail, wallpaper);
        }
    }

    [RelayCommand]
    private void OpenBrowse() => _navigation.NavigateTo(Routes.Browse);

    [RelayCommand]
    private void OpenFavorites() => _navigation.NavigateTo(Routes.Favorites);

    [RelayCommand]
    private void OpenLibrary() => _navigation.NavigateTo(Routes.Library);

    /// <summary>Stores the resource key so the message can be re-resolved after a language change.</summary>
    private void SetError(string? key, params object?[] args)
    {
        _errorKey = key;
        _errorArgs = args;
        ErrorMessage = key is null ? null : Loc.Format(key, args);
    }

    public override void DetachFromPage()
    {
        _library.Changed -= OnLibraryChanged;
        base.DetachFromPage();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = RefreshRecentAsync();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshRecentAsync());
        }
    }

    private async Task RefreshRecentAsync()
    {
        try
        {
            var recent = await _library.GetRecentlyUsedAsync(8);
            RecentlyUsed.Clear();
            foreach (var item in recent)
            {
                RecentlyUsed.Add(item);
            }

            OnPropertyChanged(nameof(HasRecent));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the recently used feed");
        }
    }
}
