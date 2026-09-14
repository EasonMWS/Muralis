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

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly BingWallpaperProvider _onlineProvider;
    private readonly MockWallpaperProvider _sampleProvider;
    private readonly IImageCacheService _imageCache;
    private readonly ILocalLibrary _library;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

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
        ILogger<HomeViewModel> logger)
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

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public bool IsInitialLoading => IsLoading && Recommended.Count == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsInitialLoading));

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

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
                ErrorMessage = "Bing is unreachable right now — showing sample wallpapers instead.";
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
            ErrorMessage = "We couldn't load wallpapers just now. Please try again.";
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
