using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;

namespace Muralis.Desktop.Launch;

/// <summary>
/// Opens the folder a pinned application lives in, with the application already selected, the way
/// Explorer's "Open file location" does. Nothing is executed: revealing a pin whose target has been
/// deleted still works, because its folder is what gets opened.
/// </summary>
/// <remarks>
/// The shell call blocks, so it runs on the thread pool — the dock asks and is told how it ended, and
/// never builds a command line itself. The path is passed as one argument built by the shell's own
/// rules, so a folder whose name contains a comma or a quote still selects the right file.
/// </remarks>
public sealed class ShellLocationRevealer : IApplicationLocationRevealer
{
    private readonly ILogger<ShellLocationRevealer> _logger;

    public ShellLocationRevealer(ILogger<ShellLocationRevealer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> RevealAsync(string target, CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<bool>(cancellationToken)
            : Task.Run(() => Reveal(target), CancellationToken.None);
    }

    private bool Reveal(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string? folder;
        try
        {
            folder = Path.GetDirectoryName(target);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            _logger.LogWarning(ex, "The location of {Target} could not be worked out", target);
            return false;
        }

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            _logger.LogWarning("The folder holding {Target} is gone", target);
            return false;
        }

        var start = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
        };
        start.ArgumentList.Add("/select," + target);

        try
        {
            Process.Start(start)?.Dispose();
            _logger.LogInformation("The location of {Target} was opened", target);
            return true;
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "The location of {Target} could not be opened", target);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "The location of {Target} could not be opened", target);
            return false;
        }
    }
}
