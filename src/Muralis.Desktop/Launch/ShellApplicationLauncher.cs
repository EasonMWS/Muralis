using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;

namespace Muralis.Desktop.Launch;

/// <summary>
/// Starts a pinned application through the shell, so per-user associations, shortcut settings and
/// elevation prompts behave exactly as they do from Explorer. The pinned settings are the only thing
/// Muralis adds: an application pinned with arguments is started with them, and one pinned without
/// them is started the way the shell would start it.
/// </summary>
/// <remarks>
/// The shell call is blocking, so it runs on the thread pool: the dock asks for a launch and is told
/// how it ended. A target that has gone comes back as missing and a refusal as failed — neither is
/// thrown, because both are things to show the user rather than crashes.
/// </remarks>
public sealed class ShellApplicationLauncher : IApplicationLauncher
{
    private readonly ILogger<ShellApplicationLauncher> _logger;

    public ShellApplicationLauncher(ILogger<ShellApplicationLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<ApplicationLaunchResult> LaunchAsync(
        ApplicationLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<ApplicationLaunchResult>(cancellationToken)
            : Task.Run(() => Launch(request), CancellationToken.None);
    }

    private ApplicationLaunchResult Launch(ApplicationLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            _logger.LogWarning("A pinned application was asked to start, but it has no target");
            return ApplicationLaunchResult.Failed("The pinned application has no target.");
        }

        if (!File.Exists(request.Target) && !Directory.Exists(request.Target))
        {
            _logger.LogWarning("The pinned application {Target} is gone", request.Target);
            return ApplicationLaunchResult.Missing(request.Target);
        }

        var start = new ProcessStartInfo(request.Target)
        {
            // UseShellExecute is the shell's own "open" — the same path a double-click in Explorer
            // takes — and it is the only way a shortcut keeps its own settings and an application keeps
            // its per-user associations.
            UseShellExecute = true,
            Verb = "open",
        };

        if (!string.IsNullOrWhiteSpace(request.Arguments))
        {
            start.Arguments = request.Arguments;
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            if (Directory.Exists(request.WorkingDirectory))
            {
                start.WorkingDirectory = request.WorkingDirectory;
            }
            else
            {
                // A folder that has been deleted would make the launch fail outright. Leaving it out
                // lets the application start where it would start on its own, which is the safer of the
                // two answers for a setting the user cannot currently fix from the dock.
                _logger.LogWarning(
                    "The start-in folder {Directory} for {Target} is gone; starting without one",
                    request.WorkingDirectory,
                    request.Target);
            }
        }

        try
        {
            Process.Start(start)?.Dispose();
            _logger.LogInformation("The pinned application {Target} was started", request.Target);
            return ApplicationLaunchResult.Launched;
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "The pinned application {Target} could not be started", request.Target);
            return ApplicationLaunchResult.Failed(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "The pinned application {Target} could not be started", request.Target);
            return ApplicationLaunchResult.Failed(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "The pinned application {Target} disappeared before it could start", request.Target);
            return ApplicationLaunchResult.Missing(request.Target);
        }
    }
}
