using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();

        FitModeCombo.SelectedItem = ViewModel.DefaultFitMode;
        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.RefreshCacheSizeCommand.Execute(null);
    }

    private void OnSystemThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.System);

    private void OnLightThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.Light);

    private void OnDarkThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.Dark);

    private void OnFitModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FitModeCombo.SelectedItem is WallpaperFitMode mode && mode != ViewModel.DefaultFitMode)
        {
            ViewModel.DefaultFitMode = mode;
        }
    }

    private void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenDownloadFolderCommand.Execute(null);

    private void OnChangeDownloadFolderClick(object sender, RoutedEventArgs e) =>
        ViewModel.ChangeDownloadFolderCommand.Execute(null);

    private void OnRefreshCacheClick(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCacheSizeCommand.Execute(null);

    private void OnClearCacheClick(object sender, RoutedEventArgs e) =>
        ViewModel.ClearCacheCommand.Execute(null);
}
