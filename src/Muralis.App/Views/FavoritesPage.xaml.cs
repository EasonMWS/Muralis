using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class FavoritesPage : Page
{
    public FavoritesPage()
    {
        ViewModel = App.GetService<FavoritesViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public FavoritesViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachFromPage();

    private void OnWallpaperItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenWallpaperCommand.Execute(e.ClickedItem as Wallpaper);
}
