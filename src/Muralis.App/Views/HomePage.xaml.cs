using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;
using System.ComponentModel;

namespace Muralis.App.Views;

public sealed partial class HomePage : Page
{
    /// <summary>
    /// The width the hero card needs before the mini desktop preview earns its place: the preview itself,
    /// the room it is set apart by, and enough left over for the headline and the actions beside it.
    /// Measured on the card rather than on the window, because the shell's navigation pane and the page's
    /// own margins are already gone by the time the card is laid out.
    /// </summary>
    private const double WidthThatFitsTheHeroPreview = 780;
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
    /// A narrow window loses the preview rather than squeezing the words: the headline is what the card is
    /// for, and a 360 pixel drawing next to a column of single characters is worse than no drawing at all.
    /// </summary>
    private void OnHeroSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        HeroPreview.Visibility = e.NewSize.Width >= WidthThatFitsTheHeroPreview
            ? Visibility.Visible
            : Visibility.Collapsed;

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
