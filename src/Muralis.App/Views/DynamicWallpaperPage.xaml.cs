using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;

namespace Muralis.App.Views;

public sealed partial class DynamicWallpaperPage : Page
{
    public DynamicWallpaperPage()
    {
        ViewModel = App.GetService<DynamicWallpaperViewModel>();
        InitializeComponent();

        Unloaded += OnUnloaded;
    }

    public DynamicWallpaperViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachFromPage();
}
