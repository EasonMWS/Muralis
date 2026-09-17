using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.UI.Motion;
using Muralis.Core.Models;
using System.Numerics;

namespace Muralis.App.Controls;

public sealed partial class WallpaperCard : UserControl
{
    public static readonly DependencyProperty WallpaperProperty = DependencyProperty.Register(
        nameof(Wallpaper),
        typeof(Wallpaper),
        typeof(WallpaperCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected),
        typeof(bool),
        typeof(WallpaperCard),
        new PropertyMetadata(false, OnIsSelectedChanged));

    private bool _pointerOver;

    public WallpaperCard()
    {
        InitializeComponent();
        Loaded += (_, _) => SetDepth(12);
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            MotionRoot.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MuralisBorderHoverBrush"];
            HoverMotion.Enter(MotionRoot);
            HoverMotion.Enter(PreviewImage, lift: false, scale: 1.025);
            SetDepth(24);
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            UpdateSelection();
            HoverMotion.Exit(MotionRoot);
            HoverMotion.Exit(PreviewImage);
            SetDepth(12);
        };
        PointerPressed += (_, _) =>
        {
            PressMotion.Down(MotionRoot);
            SetDepth(7);
        };
        PointerReleased += (_, _) =>
        {
            PressMotion.Release(MotionRoot, _pointerOver, lift: true);
            SetDepth(_pointerOver ? 24 : 12);
        };
    }

    public Wallpaper? Wallpaper
    {
        get => (Wallpaper?)GetValue(WallpaperProperty);
        set => SetValue(WallpaperProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private static void OnIsSelectedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((WallpaperCard)dependencyObject).UpdateSelection();

    private void UpdateSelection()
    {
        SelectionIndicator.Opacity = IsSelected ? 1 : 0;
        MotionRoot.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            IsSelected ? "MuralisBorderActiveBrush" : "MuralisBorderNormalBrush"];
    }

    private void SetDepth(float z) => MotionRoot.Translation = new Vector3(
        MotionRoot.Translation.X,
        MotionRoot.Translation.Y,
        z);
}
