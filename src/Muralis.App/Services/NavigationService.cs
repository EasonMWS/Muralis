using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Muralis.App.Views;
using Muralis.App.UI.Playground;
using Muralis.App.UI.Dock;

namespace Muralis.App.Services;

public interface INavigationService
{
    bool CanGoBack { get; }

    event EventHandler<NavigatedEventArgs>? Navigated;

    void Attach(Frame frame);

    bool NavigateTo(string pageKey, object? parameter = null);

    bool GoBack();
}

public sealed class NavigatedEventArgs(string pageKey, object? parameter) : EventArgs
{
    public string PageKey { get; } = pageKey;

    public object? Parameter { get; } = parameter;
}

public sealed class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;

    public NavigationService(ILogger<NavigationService> logger) => _logger = logger;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public void Attach(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _frame.Navigated += OnFrameNavigated;
    }

    public bool NavigateTo(string pageKey, object? parameter = null)
    {
        if (_frame is null)
        {
            return false;
        }

        var route = Routes.Find(pageKey);
        if (route is null)
        {
            return false;
        }

        // Avoid stacking duplicate entries when re-selecting the current page.
        if (_frame.CurrentSourcePageType == route.PageType && parameter is null)
        {
            return true;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var navigated = _frame.Navigate(route.PageType, parameter, new EntranceNavigationTransitionInfo());
        LogNavigation(pageKey, startedAt);
        return navigated;
    }

    public bool GoBack()
    {
        if (_frame?.CanGoBack != true)
        {
            return false;
        }

        var startedAt = Stopwatch.GetTimestamp();
        _frame.GoBack(new EntranceNavigationTransitionInfo());
        LogNavigation(Routes.KeyForPageType(_frame.CurrentSourcePageType) ?? "back", startedAt);
        return true;
    }

    private void LogNavigation(string pageKey, long startedAt) =>
        _logger.LogInformation(
            "Page '{Page}' built in {ElapsedMs:0} ms",
            pageKey,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var key = Routes.KeyForPageType(e.SourcePageType) ?? string.Empty;
        Navigated?.Invoke(this, new NavigatedEventArgs(key, e.Parameter));
    }
}

public sealed record NavigationRoute(string Key, Type PageType, string Title);

public static class Routes
{
    public const string Home = "home";
    public const string Browse = "browse";
    public const string Library = "library";
    public const string Favorites = "favorites";
    public const string Downloads = "downloads";
    public const string Dynamic = "dynamic";
    public const string Settings = "settings";
    public const string Detail = "detail";
    public const string DesignPlayground = "design-playground";
    public const string DockLab = "dock-lab";

    private static readonly NavigationRoute[] AllRoutes =
    [
        new(Home, typeof(HomePage), "Home"),
        new(Browse, typeof(BrowsePage), "Browse"),
        new(Library, typeof(LibraryPage), "Library"),
        new(Favorites, typeof(FavoritesPage), "Favorites"),
        new(Downloads, typeof(DownloadsPage), "Downloads"),
        new(Dynamic, typeof(DynamicWallpaperPage), "Dynamic wallpaper"),
        new(Settings, typeof(SettingsPage), "Settings"),
        new(Detail, typeof(DetailPage), "Details"),
        new(DesignPlayground, typeof(DesignPlaygroundPage), "Design playground"),
        new(DockLab, typeof(DockLabPage), "Dock Lab"),
    ];

    public static NavigationRoute? Find(string key) =>
        AllRoutes.FirstOrDefault(route => string.Equals(route.Key, key, StringComparison.Ordinal));

    public static string? KeyForPageType(Type? pageType) =>
        pageType is null ? null : AllRoutes.FirstOrDefault(route => route.PageType == pageType)?.Key;
}
