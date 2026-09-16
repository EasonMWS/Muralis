using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.App.Services.Platform;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;
using Muralis.Desktop.Shell;
using Serilog;

namespace Muralis.App;

public partial class App : Application
{
    private readonly AppHost _host = new();
    private Window? _mainWindow;

    public App()
    {
        _host.EnsureLogging();
        Log.Information("Startup: app class created at {ElapsedMs:0} ms (process start + {ProcessMs:0} ms)", StartupTrace.ElapsedMs, StartupTrace.ProcessToAppMs);

        InitializeComponent();
        Log.Information("Startup: app resources loaded at {ElapsedMs:0} ms", StartupTrace.ElapsedMs);

        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Remove the tray icon and stop timers cleanly when the process exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host.Dispose();
    }

    public static T GetService<T>()
        where T : notnull =>
        Current is App app
            ? app._host.Services.GetRequiredService<T>()
            : throw new InvalidOperationException("The application host is not available.");

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppPaths.EnsureCreated();

            // A second launch must not build a second tray, rotation timer, database writer or
            // desktop host: it wakes the running instance and exits before any service exists.
            if (!SingleInstanceGuard.IsPrimaryInstance())
            {
                Log.Information("Another Muralis instance is already running; this launch only woke it");
                Log.CloseAndFlush();
                Exit();
                return;
            }

            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Muralis {Version} starting", ThisAssemblyVersion());

            // Apply the saved display language before any XAML is parsed, then expose the
            // service as "Loc" so views can bind text through it.
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            var localization = _host.Services.GetRequiredService<LocalizationService>();
            localization.Apply(settings.Current.Language);
            Resources["Loc"] = localization;

            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            StartupTrace.Log(logger, "window created");
            _mainWindow.Activate();
            StartupTrace.Log(logger, "window activated");

            // Queued at low priority, so it runs once the startup work (page load, layout,
            // first render) has been processed: the practical "window is interactive" mark.
            _mainWindow.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => StartupTrace.Log(logger, "ui idle (first frame done)"));

            _host.Services.GetRequiredService<IThemeService>().ApplyFromSettings();

            // The tray icon and rotation timer need the window to exist first.
            _host.Services.GetRequiredService<TrayService>();
            _host.Services.GetRequiredService<RotationService>().ApplySettings();

            // Explorer can restart at any time; the shell event source turns that into events.
            // TrayService listens for its icon, and the single DesktopShell instance created here
            // listens to re-mount desktop surfaces on the new WorkerW. No one else responds to it.
            _host.Services.GetRequiredService<IDesktopShell>();

            // Bringing the video wallpaper back is off the startup path: the desktop shell owns
            // its own thread, and a missing or broken file must not delay the window.
            _ = RestoreVideoWallpaperAsync(logger);

            // The canvas prototype comes back the same way when it was left switched on.
            _ = RestoreDesktopCanvasAsync(logger);

            // SQLite and the catalog are not needed for the first frame; loading them in
            // the background keeps the window's appear time short. Pages listening to
            // ILocalLibrary.Changed refresh as soon as the catalog is ready.
            _ = InitializeCatalogInBackgroundAsync(logger);
        }
        catch (Exception ex)
        {
            // Startup failed before logging subscribers exist; write to the debugger and rethrow
            // so the failure is visible instead of leaving a headless process behind.
            System.Diagnostics.Debug.WriteLine($"Fatal startup failure: {ex}");
            throw;
        }
    }

    private async Task InitializeCatalogInBackgroundAsync(Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            await _host.InitializeCatalogAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load the wallpaper catalog");
        }
    }

    private async Task RestoreVideoWallpaperAsync(Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var settings = _host.Services.GetRequiredService<ISettingsService>().Current.VideoWallpaper;
            if (!settings.Enabled)
            {
                return;
            }

            if (!File.Exists(settings.VideoPath))
            {
                logger.LogWarning("The saved video wallpaper is gone: {Path}", settings.VideoPath);
                return;
            }

            var status = await _host.Services
                .GetRequiredService<IVideoWallpaperService>()
                .StartAsync(settings.VideoPath, settings.Muted)
                .ConfigureAwait(true);

            if (status.State == VideoWallpaperState.Failed)
            {
                logger.LogWarning("The video wallpaper could not be restored: {Error}", status.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The video wallpaper could not be restored");
        }
    }

    private async Task RestoreDesktopCanvasAsync(Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            if (!_host.Services.GetRequiredService<ISettingsService>().Current.DesktopCanvas.Enabled)
            {
                return;
            }

            var status = await _host.Services
                .GetRequiredService<IDesktopCanvasService>()
                .EnableAsync()
                .ConfigureAwait(true);

            if (status.State == CanvasPrototypeState.Failed)
            {
                logger.LogWarning("The desktop canvas could not be restored: {Error}", status.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The desktop canvas could not be restored");
        }
    }

    private static string ThisAssemblyVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception, "Unhandled XAML exception");

        // Keep the app alive when the UI can still function; the error is logged either way.
        e.Handled = true;
        _ = ShowCrashDialogAsync(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogFatal(exception, "Unhandled domain exception");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatal(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void LogFatal(Exception exception, string message)
    {
        try
        {
            _host.Services.GetRequiredService<ILogger<App>>().LogCritical(exception, "{Message}", message);
        }
        catch (InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"{message}: {exception}");
        }
    }

    private async Task ShowCrashDialogAsync(Exception exception)
    {
        try
        {
            var dialogs = _host.Services.GetRequiredService<IDialogService>();
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            await dialogs.ShowMessageAsync(
                localization.Get("Crash_Title"),
                localization.Format("Crash_Message", exception.Message));
        }
        catch (Exception dialogFailure)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show the crash dialog: {dialogFailure}");
        }
    }
}
