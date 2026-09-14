using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Providers;

namespace Muralis.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly BingWallpaperProvider _onlineProvider;
    private readonly MockWallpaperProvider _sampleProvider;
    private readonly IImageCacheService _imageCache;
    private readonly ILocalLibrary _library;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private string? _errorKey;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial Wallpaper? Featured { get; set; }

    public HomeViewModel(
        BingWallpaperProvider onlineProvider,
        MockWallpaperProvider sampleProvider,
        IImageCacheService imageCache,
        ILocalLibrary library,
        INavigationService navigation,
        ILocalizationService localization,
        ILogger<HomeViewModel> logger)
        : base(localization)
    {
        _onlineProvider = onlineProvider;
        _sampleProvider = sampleProvider;
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
            ErrorMessage = Loc.Get(_errorKey);
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

        try
        {
            IReadOnlyList<Wallpaper> items;
            try
            {
                items = await _onlineProvider
                    .GetWallpapersAsync(new WallpaperQuery { PageSize = 24 }, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bing feed unavailable; falling back to sample wallpapers");
                SetError("Home_Error_FeedUnavailable");
                items = await _sampleProvider
                    .GetWallpapersAsync(new WallpaperQuery { PageSize = 24 }, cancellationToken)
                    .ConfigureAwait(true);
            }

            Recommended.Clear();
            foreach (var item in items)
            {
                Recommended.Add(item);
            }

            Featured = Recommended.FirstOrDefault();
            await RefreshRecentAsync();

            _logger.LogInformation("Home feed loaded with {Count} wallpapers", Recommended.Count);

            // Pull thumbnails in the background so the hero and cards fill in as files arrive.
            _ = _imageCache.WarmThumbnailsAsync(Recommended.ToList(), cancellationToken);
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
    private void SetError(string? key)
    {
        _errorKey = key;
        ErrorMessage = key is null ? null : Loc.Get(key);
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
