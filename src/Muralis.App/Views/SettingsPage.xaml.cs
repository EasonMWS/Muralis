using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Services;
using Muralis.App.ViewModels;
using Muralis.Core.Models;

namespace Muralis.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _initializing = true;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();

        ApplyOptionSources();
        ApplyRotationState();
        _initializing = false;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.RotationEnabled) or nameof(SettingsViewModel.RotationStatusText))
            {
                ApplyRotationState();
            }
            else if (e.PropertyName is nameof(SettingsViewModel.FitModes)
                or nameof(SettingsViewModel.IntervalOptions)
                or nameof(SettingsViewModel.RotationSourceOptions)
                or nameof(SettingsViewModel.LanguageOptions))
            {
                // Option labels were re-localized; rebuild the combo boxes and keep the selection.
                ApplyOptionSources();
            }
        };

        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.RefreshCacheSizeCommand.Execute(null);
        await ViewModel.RefreshMonitorsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// (Re)builds the combo box item sources from the view model. Called once on load and
    /// again whenever the localized option labels change, so selections are preserved.
    /// </summary>
    private void ApplyOptionSources()
    {
        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            FitModeCombo.ItemsSource = ViewModel.FitModes;
            FitModeCombo.SelectedItem = ViewModel.FitModes.FirstOrDefault(option => option.Mode == ViewModel.DefaultFitMode);

            IntervalCombo.ItemsSource = ViewModel.IntervalOptions.Select(option => option.Label).ToList();
            IntervalCombo.SelectedIndex = IndexOfInterval(ViewModel.CurrentIntervalOption);

            RotationSourceCombo.ItemsSource = ViewModel.RotationSourceOptions.Select(option => option.Label).ToList();
            RotationSourceCombo.SelectedIndex = ViewModel.UseFavoritesSource ? 0 : 1;

            LanguageCombo.ItemsSource = ViewModel.LanguageOptions;
            LanguageCombo.SelectedItem = ViewModel.LanguageOptions
                .FirstOrDefault(option => option.Preference == ViewModel.SelectedLanguageOption?.Preference)
                ?? ViewModel.LanguageOptions[0];
        }
        finally
        {
            _initializing = wasInitializing;
        }
    }

    private int IndexOfInterval(IntervalOption current)
    {
        for (var i = 0; i < ViewModel.IntervalOptions.Count; i++)
        {
            if (ViewModel.IntervalOptions[i].Value == current.Value)
            {
                return i;
            }
        }

        return 1;
    }

    private void ApplyRotationState()
    {
        RotationOptionsPanel.IsEnabled = ViewModel.RotationEnabled;
        RotationSummaryText.Text = ViewModel.RotationStatusText;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && LanguageCombo.SelectedItem is LanguageOption option)
        {
            ViewModel.SelectedLanguageOption = option;
        }
    }

    private void OnSystemThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.System);

    private void OnLightThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.Light);

    private void OnDarkThemeClick(object sender, RoutedEventArgs e) => ViewModel.SelectTheme(AppTheme.Dark);

    private void OnFitModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && FitModeCombo.SelectedItem is FitModeOption option && option.Mode != ViewModel.DefaultFitMode)
        {
            ViewModel.DefaultFitMode = option.Mode;
        }
    }

    private void OnIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || IntervalCombo.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SetRotationInterval(ViewModel.IntervalOptions[IntervalCombo.SelectedIndex].Value);
    }

    private void OnRotationSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || RotationSourceCombo.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.UseFavoritesSource = RotationSourceCombo.SelectedIndex == 0;
        ApplyRotationState();
    }

    private async void OnChooseRotationFolderClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ChooseRotationFolderCommand.ExecuteAsync(null);
        _initializing = true;
        RotationSourceCombo.SelectedIndex = 1;
        _initializing = false;
        ApplyRotationState();
    }

    private void OnShuffleNowClick(object sender, RoutedEventArgs e) =>
        ViewModel.ShuffleNowCommand.Execute(null);

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        ViewModel.ExitApplicationCommand.Execute(null);

    private void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenDownloadFolderCommand.Execute(null);

    private void OnChangeDownloadFolderClick(object sender, RoutedEventArgs e) =>
        ViewModel.ChangeDownloadFolderCommand.Execute(null);

    private void OnRefreshCacheClick(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCacheSizeCommand.Execute(null);

    private void OnClearCacheClick(object sender, RoutedEventArgs e) =>
        ViewModel.ClearCacheCommand.Execute(null);
}
