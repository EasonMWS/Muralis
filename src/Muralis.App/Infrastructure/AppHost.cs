using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Muralis.App.Services;
using Muralis.App.Services.Platform;
using Muralis.App.ViewModels;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Providers;
using Muralis.Core.Repositories;
using Muralis.Core.Services;

namespace Muralis.App.Infrastructure;

/// <summary>
/// Owns the dependency injection container and process-wide logging.
/// </summary>
public sealed class AppHost
{
    private ServiceProvider? _provider;

    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The application host has not been started yet.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ConfigureSerilog();
        _provider = BuildProvider();

        await _provider
            .GetRequiredService<ISettingsService>()
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        await _provider
            .GetRequiredService<ILocalLibrary>()
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        // Shared HTTP client for providers, downloads and thumbnail caching.
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(90) });

        // Core services (platform-agnostic).
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<MockWallpaperProvider>();
        services.AddSingleton<BingWallpaperProvider>();
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<MockWallpaperProvider>());
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<BingWallpaperProvider>());
        services.AddSingleton<IWallpaperRepository>(sp => new SqliteWallpaperRepository(
            sp.GetRequiredService<ILogger<SqliteWallpaperRepository>>()));
        services.AddSingleton<ILocalLibrary, LocalLibrary>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IImageCacheService, ImageCacheService>();

        // Application services.
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();

        // Windows platform services.
        services.AddSingleton<IImageFormatService, ImageFormatService>();
        services.AddSingleton<IWallpaperService, WindowsWallpaperService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<RotationService>();
        services.AddSingleton<TrayService>();

        // Shell and view models.
        services.AddSingleton<MainWindow>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<BrowseViewModel>();
        services.AddTransient<DetailViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void ConfigureSerilog()
    {
        var logFilePath = Path.Combine(AppPaths.LogsDirectory, "muralis-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
