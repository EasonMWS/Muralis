using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Muralis.Core.Abstractions;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Launch-at-sign-in through the per-user Run key (the standard mechanism for
/// unpackaged desktop apps; no package identity required).
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Muralis";

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger) => _logger = logger;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the startup registration");
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException($@"The registry key HKCU\{RunKeyPath} is not accessible.");

            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The application path is not available.");
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
                _logger.LogInformation("Startup registration added for {Path}", executable);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Startup registration removed");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not update the startup registration");
            throw;
        }
    }
}
