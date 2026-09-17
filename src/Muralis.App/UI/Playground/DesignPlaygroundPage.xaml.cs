using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Muralis.App.Services;
using Muralis.Core.Models;

namespace Muralis.App.UI.Playground;

public sealed partial class DesignPlaygroundPage : Page
{
    public DesignPlaygroundPage()
    {
        SampleWallpaper = new Wallpaper
        {
            Id = "playground:atmosphere",
            Title = "Cyan Hour",
            Width = 3840,
            Height = 2160,
            IsFavorite = true,
            Tags = ["Design reference"],
        };

        InitializeComponent();
    }

    public Wallpaper SampleWallpaper { get; }

    private void OnOpenDockLabClick(object sender, RoutedEventArgs e) =>
        App.GetService<INavigationService>().NavigateTo(Routes.DockLab);
}
