using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        INavigationService navigation,
        IThemeService themeService,
        ISettingsService settingsService,
        WindowContext windowContext,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _navigation = navigation;
        _logger = logger;

        windowContext.MainWindow = this;

        _navigation.Attach(RootFrame);
        _navigation.Navigated += OnNavigated;

        WindowHelper.ConfigureInitialPlacement(this);

        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        _navigation.NavigateTo(Routes.Home);
    }

    /// <summary>
    /// ItemInvoked (unlike SelectionChanged) also fires when the already-selected item is
    /// clicked again — for example to leave a detail page and return to the section.
    /// </summary>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string key } && !_navigation.NavigateTo(key))
        {
            _logger.LogWarning("Navigation to '{Key}' was not possible", key);
        }
    }

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        AppTitleBar.IsBackButtonVisible = _navigation.CanGoBack;

        // Detail and other sub-pages are not represented in the navigation pane;
        // keep the previously selected item in that case.
        var matchingItem = FindNavigationItem(e.PageKey);
        if (matchingItem is not null && !ReferenceEquals(RootNavigationView.SelectedItem, matchingItem))
        {
            RootNavigationView.SelectedItem = matchingItem;
        }
    }

    private NavigationViewItem? FindNavigationItem(string pageKey)
    {
        foreach (var item in RootNavigationView.MenuItems.Concat(RootNavigationView.FooterMenuItems))
        {
            if (item is NavigationViewItem navItem && string.Equals(navItem.Tag as string, pageKey, StringComparison.Ordinal))
            {
                return navItem;
            }
        }

        return null;
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (!_navigation.GoBack())
        {
            _logger.LogDebug("Back requested but the navigation stack is empty");
        }
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args) =>
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
}
