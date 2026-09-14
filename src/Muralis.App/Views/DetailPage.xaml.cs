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
        Loaded += OnLoaded;
    }

    public DetailViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load(e.Parameter as Wallpaper);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadMonitorsCommand.ExecuteAsync(null);
    }

    private void OnSetAsWallpaperClick(object sender, RoutedEventArgs e) =>
        ViewModel.SetAsWallpaperCommand.Execute(null);

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleFavoriteCommand.Execute(null);

    private void OnOpenInExplorerClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenInExplorerCommand.Execute(null);

    private void OnRemoveFromLibraryClick(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveFromLibraryCommand.Execute(null);

    private void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedMonitor = MonitorCombo.SelectedItem as MonitorInfo;
}
