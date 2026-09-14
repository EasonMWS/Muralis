using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Muralis.App.Views;

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
    private Frame? _frame;

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

        return _frame.Navigate(route.PageType, parameter, new EntranceNavigationTransitionInfo());
    }

    public bool GoBack()
    {
        if (_frame?.CanGoBack != true)
        {
            return false;
        }

        _frame.GoBack(new EntranceNavigationTransitionInfo());
        return true;
    }

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
    public const string Settings = "settings";
    public const string Detail = "detail";

    private static readonly NavigationRoute[] AllRoutes =
    [
        new(Home, typeof(HomePage), "Home"),
        new(Browse, typeof(BrowsePage), "Browse"),
        new(Library, typeof(LibraryPage), "Library"),
        new(Favorites, typeof(FavoritesPage), "Favorites"),
        new(Settings, typeof(SettingsPage), "Settings"),
        new(Detail, typeof(DetailPage), "Details"),
    ];

    public static NavigationRoute? Find(string key) =>
        AllRoutes.FirstOrDefault(route => string.Equals(route.Key, key, StringComparison.Ordinal));

    public static string? KeyForPageType(Type? pageType) =>
        pageType is null ? null : AllRoutes.FirstOrDefault(route => route.PageType == pageType)?.Key;
}
