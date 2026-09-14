using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.Core.Models;

namespace Muralis.App.Controls;

public sealed partial class WallpaperCard : UserControl
{
    public static readonly DependencyProperty WallpaperProperty = DependencyProperty.Register(
        nameof(Wallpaper),
        typeof(Wallpaper),
        typeof(WallpaperCard),
        new PropertyMetadata(null));

    public WallpaperCard() => InitializeComponent();

    public Wallpaper? Wallpaper
    {
        get => (Wallpaper?)GetValue(WallpaperProperty);
        set => SetValue(WallpaperProperty, value);
    }
}
