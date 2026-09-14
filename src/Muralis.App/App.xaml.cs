using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.Core.Helpers;

namespace Muralis.App;

public partial class App : Application
{
    private readonly AppHost _host = new();
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();

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
            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Muralis {Version} starting", ThisAssemblyVersion());

            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _mainWindow.Activate();

            _host.Services.GetRequiredService<IThemeService>().ApplyFromSettings();

            // The tray icon and rotation timer need the window to exist first.
            _host.Services.GetRequiredService<TrayService>();
            _host.Services.GetRequiredService<RotationService>().ApplySettings();
        }
        catch (Exception ex)
        {
            // Startup failed before logging subscribers exist; write to the debugger and rethrow
            // so the failure is visible instead of leaving a headless process behind.
            System.Diagnostics.Debug.WriteLine($"Fatal startup failure: {ex}");
            throw;
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
            await dialogs.ShowMessageAsync(
                "Something went wrong",
                $"Muralis hit an unexpected error and logged the details.\n\n{exception.Message}");
        }
        catch (Exception dialogFailure)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show the crash dialog: {dialogFailure}");
        }
    }
}
