using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Muralis.App.Infrastructure;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.Services;

public interface IThemeService
{
    void Apply(AppTheme theme);

    void ApplyFromSettings();
}

public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private readonly WindowContext _windowContext;
    private readonly ILogger<ThemeService> _logger;

    public ThemeService(
        ISettingsService settingsService,
        WindowContext windowContext,
        ILogger<ThemeService> logger)
    {
        _settingsService = settingsService;
        _windowContext = windowContext;
        _logger = logger;

        _settingsService.SettingsChanged += (_, settings) => Apply(settings.Theme);
    }

    public void Apply(AppTheme theme)
    {
        if (_windowContext.MainWindow?.Content is not FrameworkElement root)
        {
            _logger.LogDebug("Theme change requested before the main window was ready; it will apply on launch");
            return;
        }

        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        _logger.LogDebug("Theme applied: {Theme}", theme);
    }

    public void ApplyFromSettings() => Apply(_settingsService.Current.Theme);
}
