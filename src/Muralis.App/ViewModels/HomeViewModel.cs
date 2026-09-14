using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IWallpaperProvider _wallpaperProvider;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial Wallpaper? Featured { get; set; }

    public HomeViewModel(
        IWallpaperProvider wallpaperProvider,
        INavigationService navigation,
        ILogger<HomeViewModel> logger)
    {
        _wallpaperProvider = wallpaperProvider;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<Wallpaper> Recommended { get; } = [];

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
            var items = await _wallpaperProvider
                .GetWallpapersAsync(new WallpaperQuery { PageSize = 24 }, cancellationToken)
                .ConfigureAwait(true);

            Recommended.Clear();
            foreach (var item in items)
            {
                Recommended.Add(item);
            }

            Featured = Recommended.FirstOrDefault();
            _logger.LogInformation("Home feed loaded with {Count} wallpapers", Recommended.Count);
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
}
