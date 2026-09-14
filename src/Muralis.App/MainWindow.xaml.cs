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
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;
    private readonly TrayService _trayService;
    private readonly ILogger<MainWindow> _logger;
    private bool _allowClose;

    public MainWindow(
        INavigationService navigation,
        IThemeService themeService,
        ISettingsService settingsService,
        TrayService trayService,
        WindowContext windowContext,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _navigation = navigation;
        _themeService = themeService;
        _settingsService = settingsService;
        _trayService = trayService;
        _logger = logger;

        windowContext.MainWindow = this;

        ConfigureTitleBar();
        ApplyWindowIcon();

        _navigation.Attach(RootFrame);
        _navigation.Navigated += OnNavigated;

        WindowHelper.ConfigureInitialPlacement(this);

        AppWindow.Closing += OnWindowClosing;

        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        _navigation.NavigateTo(Routes.Home);
    }

    /// <summary>
    /// Replaces the system title bar with the WinUI <see cref="TitleBar"/> control.
    /// Without this the system bar stays visible above the custom one, showing the
    /// default Windows icon instead of the Muralis brand.
    /// </summary>
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    /// <summary>
    /// Sets the window icon used by the taskbar, Alt+Tab and (when shown) the system
    /// title bar. The icon inside the app's own title bar comes from the control.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
            else
            {
                _logger.LogWarning("Window icon not found at {Path}", iconPath);
            }
        }
        catch (Exception ex)
        {
            // A missing window icon must never keep the app from starting.
            _logger.LogWarning(ex, "Could not apply the window icon");
        }
    }

    /// <summary>Quits the application even when close-to-tray is enabled (tray menu / settings).</summary>
    public void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose || !_settingsService.Current.CloseToTray)
        {
            return;
        }

        // Keep running in the tray so rotation and quick access stay available.
        args.Cancel = true;
        _trayService.HideMainWindow();
        _logger.LogInformation("Window hidden to the tray");
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
