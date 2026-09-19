using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;
using System.ComponentModel;

namespace Muralis.App.Views;

public sealed partial class HomePage : Page
{
    /// <summary>
    /// The preview moves below the product copy before either side becomes cramped. It stays present and
    /// scales as one drawing instead of disappearing, because the Native/Muralis comparison is part of the
    /// hero's explanation rather than decoration.
    /// </summary>
    private const double WidthThatStacksTheHero = 860;
    private const double WidthThatStacksFeatures = 560;
    private const string WindowsDesktopState = "WindowsDesktop";
    private const string MuralisDesktopState = "MuralisDesktop";

    public HomePage()
    {
        ViewModel = App.GetService<HomeViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public HomeViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnHeroStateChanged;
        ViewModel.DetachFromPage();
    }

    /// <summary>
    /// Puts the hero's own drawing into the state the mode is in: the desktop it draws, the accent that
    /// marks the running one. Nothing in the card re-evaluates on its own, so without this the card would
    /// say one thing and draw another, which is the one thing a card about the state of the desktop may not
    /// do. The states themselves keep the motion the design tokens describe.
    /// </summary>
    private void OnHeroStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.IsHeroActive) or nameof(HomeViewModel.ShowsWindowsDesktop))
        {
            MoveHeroTo(ViewModel.IsHeroActive ? MuralisDesktopState : WindowsDesktopState, useTransitions: true);
        }
    }

    private bool MoveHeroTo(string state, bool useTransitions) =>
        VisualStateManager.GoToState(this, state, useTransitions);

    /// <summary>
    /// Reflows the flagship card without clipping: medium windows stack the comparison below the copy, and
    /// very narrow windows turn the two-column capability summary into four compact rows.
    /// </summary>
    private void OnHeroSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < WidthThatStacksTheHero;
        Grid.SetRow(HeroCopy, 0);
        Grid.SetColumn(HeroCopy, 0);
        Grid.SetColumnSpan(HeroCopy, stacked ? 2 : 1);
        HeroCopy.Margin = stacked
            ? new Thickness(28, 30, 28, 18)
            : new Thickness(40, 34, 24, 34);

        Grid.SetRow(HeroPreview, stacked ? 1 : 0);
        Grid.SetColumn(HeroPreview, stacked ? 0 : 1);
        Grid.SetColumnSpan(HeroPreview, stacked ? 2 : 1);
        HeroPreview.Width = stacked ? double.NaN : 420;
        HeroPreview.Height = stacked ? 220 : 270;
        HeroPreview.Margin = stacked
            ? new Thickness(28, 0, 28, 28)
            : new Thickness(12, 30, 30, 30);
        HeroPreview.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;

        var stackFeatures = e.NewSize.Width < WidthThatStacksFeatures;
        PlaceFeature(HeroDockFeature, stackFeatures ? 0 : 0, 0, stackFeatures);
        PlaceFeature(HeroShelfFeature, stackFeatures ? 1 : 0, stackFeatures ? 0 : 1, stackFeatures);
        PlaceFeature(HeroMotionFeature, stackFeatures ? 2 : 1, 0, stackFeatures);
        PlaceFeature(HeroWidgetsFeature, stackFeatures ? 3 : 1, stackFeatures ? 0 : 1, stackFeatures);
    }

    private static void PlaceFeature(FrameworkElement feature, int row, int column, bool fullWidth)
    {
        Grid.SetRow(feature, row);
        Grid.SetColumn(feature, column);
        Grid.SetColumnSpan(feature, fullWidth ? 2 : 1);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // The page can be opened on a desktop whose mode is already running, and the state is taken before
        // the first frame rather than faded into: a card that has not moved yet must not animate to where
        // it already is.
        MoveHeroTo(ViewModel.IsHeroActive ? MuralisDesktopState : WindowsDesktopState, useTransitions: false);
        ViewModel.PropertyChanged += OnHeroStateChanged;

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
