using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Muralis.App.Infrastructure;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.Services;

public interface IThemeService
{
    ElementTheme CurrentElementTheme { get; }

    void Apply(AppTheme theme);

    void ApplyFromSettings();

    void RegisterRoot(FrameworkElement root);

    void UnregisterRoot(FrameworkElement root);
}

public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private readonly WindowContext _windowContext;
    private readonly ILogger<ThemeService> _logger;
    private readonly HashSet<FrameworkElement> _roots = [];

    public ThemeService(
        ISettingsService settingsService,
        WindowContext windowContext,
        ILogger<ThemeService> logger)
    {
        _settingsService = settingsService;
        _windowContext = windowContext;
        _logger = logger;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public ElementTheme CurrentElementTheme => ToElementTheme(_settingsService.Current.Theme);

    public void Apply(AppTheme theme)
    {
        if (_windowContext.MainWindow?.Content is not FrameworkElement mainRoot)
        {
            _logger.LogDebug("Theme change requested before the main window was ready; it will apply on launch");
            return;
        }

        _roots.Add(mainRoot);
        var requested = ToElementTheme(theme);
        foreach (var root in _roots.ToArray())
        {
            root.RequestedTheme = requested;
        }

        _logger.LogDebug("Theme applied to {RootCount} window roots: {Theme}", _roots.Count, theme);
    }

    public void ApplyFromSettings() => Apply(_settingsService.Current.Theme);

    /// <summary>
    /// Every XAML window gets an explicit, synchronized theme context. Application resources contain
    /// shared ThemeResource-backed brush instances; allowing a secondary window to resolve them under
    /// a different theme can otherwise recolor controls in the main window.
    /// </summary>
    public void RegisterRoot(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots.Add(root);
        root.RequestedTheme = CurrentElementTheme;
    }

    public void UnregisterRoot(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots.Remove(root);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var window = _windowContext.MainWindow;
        if (window is null || window.DispatcherQueue.HasThreadAccess)
        {
            Apply(settings.Theme);
            return;
        }

        // Settings may be persisted by desktop recovery or the tray. Theme is UI state, so its
        // reaction must return to the window thread even when the changed setting is unrelated.
        window.DispatcherQueue.TryEnqueue(() => Apply(settings.Theme));
    }

    private static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
