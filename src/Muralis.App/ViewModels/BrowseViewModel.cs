using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class BrowseViewModel : ObservableObject
{
    private readonly IWallpaperProvider _wallpaperProvider;
    private readonly INavigationService _navigation;
    private readonly ILogger<BrowseViewModel> _logger;
    private CancellationTokenSource? _searchDebounce;
    private readonly bool _isInitialized;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    public BrowseViewModel(
        IWallpaperProvider wallpaperProvider,
        INavigationService navigation,
        ILogger<BrowseViewModel> logger)
    {
        _wallpaperProvider = wallpaperProvider;
        _navigation = navigation;
        _logger = logger;

        SearchText = string.Empty;
        _isInitialized = true;
    }

    public ObservableCollection<Wallpaper> Items { get; } = [];

    public string ProviderName => _wallpaperProvider.DisplayName;

    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0;

    public string ResultSummary => Items.Count == 1 ? "1 wallpaper" : $"{Items.Count} wallpapers";

    public bool IsInitialLoading => IsLoading && Items.Count == 0;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private Task LoadAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

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

        IsLoading = true;
        ErrorMessage = null;
        NotifyStateChanged();

        try
        {
            var items = await _wallpaperProvider
                .GetWallpapersAsync(new WallpaperQuery { SearchText = SearchText, PageSize = 60 }, cancellationToken)
                .ConfigureAwait(true);

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            _logger.LogInformation("Browse loaded {Count} wallpapers (search: '{Search}')", Items.Count, SearchText);
        }
        catch (OperationCanceledException)
        {
            // Superseded or the page was left.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the browse feed");
            ErrorMessage = "We couldn't load wallpapers. Please try again.";
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(ResultSummary));
    }
}
