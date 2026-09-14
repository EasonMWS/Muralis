using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = App.GetService<HomeViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public HomeViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachFromPage();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnFeaturedDetailsClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenWallpaperCommand.Execute(ViewModel.Featured);

    private void OnBrowseAllClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenBrowseCommand.Execute(null);

    private void OnFavoritesTileClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenFavoritesCommand.Execute(null);

    private void OnLibraryTileClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenLibraryCommand.Execute(null);

    private void OnWallpaperItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenWallpaperCommand.Execute(e.ClickedItem as Wallpaper);
}
