using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class DetailPage : Page
{
    public DetailPage()
    {
        ViewModel = App.GetService<DetailViewModel>();
        InitializeComponent();
    }

    public DetailViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load(e.Parameter as Wallpaper);
    }

    private void OnSetAsWallpaperClick(object sender, RoutedEventArgs e) =>
        ViewModel.SetAsWallpaperCommand.Execute(null);

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleFavoriteCommand.Execute(null);

    private void OnOpenInExplorerClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenInExplorerCommand.Execute(null);
}
