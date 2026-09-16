using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Muralis.App.Services;
using Muralis.App.Services.Platform;
using Muralis.App.ViewModels;
using Muralis.Core.Abstractions;
using Muralis.Core.Canvas;
using Muralis.Core.Desktop;
using Muralis.Core.Helpers;
using Muralis.Core.Networking;
using Muralis.Core.Providers;
using Muralis.Core.Repositories;
using Muralis.Core.Services;
using Muralis.Desktop.Input;
using Muralis.Desktop.Items;
using Muralis.Desktop.Shell;
using Muralis.Desktop.Surfaces.Compatibility;

namespace Muralis.App.Infrastructure;

/// <summary>
/// Owns the dependency injection container and process-wide logging.
/// Disposing the host stops background services (rotation timer, tray icon).
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Lock _loggingLock = new();
    private ServiceProvider? _provider;
    private bool _loggingConfigured;

    public void Dispose()
    {
        _provider?.Dispose();
        _provider = null;
        Log.CloseAndFlush();
    }

    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The application host has not been started yet.");

    /// <summary>
    /// Sets up Serilog. Safe to call more than once; the first call wins so logging can
    /// start as early as the application class constructor.
    /// </summary>
    public void EnsureLogging()
    {
        lock (_loggingLock)
        {
            if (_loggingConfigured)
            {
                return;
            }

            ConfigureSerilog();
            _loggingConfigured = true;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureLogging();
        _provider = BuildProvider();

        var logger = _provider.GetRequiredService<ILogger<AppHost>>();
        StartupTrace.Log(logger, "host built");

        await _provider
            .GetRequiredService<ISettingsService>()
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        StartupTrace.Log(logger, "settings loaded");
    }

    /// <summary>
    /// Loads the wallpaper catalog (SQLite). Runs after the window is on screen so the
    /// database stays off the startup path; pages refresh through
    /// <see cref="ILocalLibrary.Changed"/> once it finishes.
    /// </summary>
    public async Task InitializeCatalogAsync(CancellationToken cancellationToken = default)
    {
        var provider = _provider ?? throw new InvalidOperationException("The application host has not been started yet.");
        var logger = provider.GetRequiredService<ILogger<AppHost>>();

        await provider
            .GetRequiredService<ILocalLibrary>()
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);

        StartupTrace.Log(logger, "catalog loaded");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        // Shared HTTP pipeline for providers, downloads and thumbnails: transient failures
        // are retried with backoff, and provider JSON responses are cached on disk so
        // browsing does not re-hit rate-limited APIs.
        services.AddSingleton<IMetadataCache, MetadataCache>();
        services.AddSingleton<IProviderConfiguration, ProviderConfiguration>();
        services.AddSingleton(sp =>
        {
            var httpClient = new HttpClient(
                new HttpRetryHandler(
                    sp.GetRequiredService<ILogger<HttpRetryHandler>>(),
                    new MetadataCacheHandler(
                        sp.GetRequiredService<IMetadataCache>(),
                        sp.GetRequiredService<ILogger<MetadataCacheHandler>>(),
                        new HttpClientHandler())))
            {
                Timeout = TimeSpan.FromSeconds(90),
            };

            // Identifies the app to the APIs it calls; GitHub rejects requests without it.
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.DisplayName}/{AppInfo.Version} (+{AppInfo.GitHubUrl})");
            return httpClient;
        });

        // Core services (platform-agnostic).
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IUpdateChecker>(sp => new UpdateChecker(
            sp.GetRequiredService<HttpClient>(),
            AppInfo.LatestReleaseApiUrl,
            sp.GetRequiredService<ILogger<UpdateChecker>>()));

        // Wallpaper sources, in the order the UI lists them.
        services.AddSingleton<BingWallpaperProvider>();
        services.AddSingleton<WallhavenWallpaperProvider>();
        services.AddSingleton<ApodWallpaperProvider>();
        services.AddSingleton<MockWallpaperProvider>();
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<BingWallpaperProvider>());
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<WallhavenWallpaperProvider>());
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<ApodWallpaperProvider>());
        services.AddSingleton<IWallpaperProvider>(sp => sp.GetRequiredService<MockWallpaperProvider>());
        services.AddSingleton<WallpaperProviderManager>();

        services.AddSingleton<IWallpaperRepository>(sp => new SqliteWallpaperRepository(
            sp.GetRequiredService<ILogger<SqliteWallpaperRepository>>()));
        services.AddSingleton<ILocalLibrary, LocalLibrary>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IDownloadQueue, DownloadQueueService>();
        services.AddSingleton<IImageCacheService, ImageCacheService>();

        // Application services.
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<ActivationWindow>();

        // Windows platform services.
        services.AddSingleton<IImageFormatService, ImageFormatService>();
        services.AddSingleton<IWallpaperService, WindowsWallpaperService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<ShellEventSource>();
        services.AddSingleton<DesktopPointerRouter>();
        services.AddSingleton<IDesktopShell, DesktopShell>();
        services.AddSingleton<IVideoWallpaperService, VideoWallpaperServiceAdapter>();
        services.AddSingleton(sp => new DesktopLayoutStore(sp.GetRequiredService<ILogger<DesktopLayoutStore>>()));
        services.AddSingleton<IDesktopItemLauncher, ShellItemLauncher>();
        services.AddSingleton<IDesktopCanvasService, DesktopCanvasServiceAdapter>();
        services.AddSingleton<RotationService>();
        services.AddSingleton<TrayService>();

        // Shell and view models.
        services.AddSingleton<MainWindow>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<BrowseViewModel>();
        services.AddTransient<DetailViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<DynamicWallpaperViewModel>();
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
