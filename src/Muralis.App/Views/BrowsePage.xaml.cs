using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class BrowsePage : Page
{
    public BrowsePage()
    {
        ViewModel = App.GetService<BrowseViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
    }

    public BrowseViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);

    private void OnWallpaperItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenWallpaperCommand.Execute(e.ClickedItem as Wallpaper);
}
