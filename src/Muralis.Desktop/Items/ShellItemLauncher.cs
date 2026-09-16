using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;

namespace Muralis.Desktop.Items;

/// <summary>
/// Opens desktop items through the shell, so a program runs, a document opens with whatever the
/// system associates with it, a folder opens in Explorer and an address opens in the default
/// browser. Launches always come from an explicit user gesture and always go through the shell's
/// own association — Muralis never builds a command line out of item data, never passes arguments
/// and never runs anything by itself.
/// </summary>
/// <remarks>
/// The shell call is blocking, so it runs on the thread pool: the desktop layer's thread only ever
/// asks for a launch and is told how it ended. One launch per user gesture; holding two items
/// down at once is impossible, so no throttling is needed beyond the gesture itself.
/// </remarks>
public sealed class ShellItemLauncher : IDesktopItemLauncher
{
    private readonly ILogger<ShellItemLauncher> _logger;

    public ShellItemLauncher(ILogger<ShellItemLauncher> logger)
    {
        _logger = logger;
    }

    public Task<DesktopItemLaunchResult> LaunchAsync(DesktopItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<DesktopItemLaunchResult>(cancellationToken)
            : Task.Run(() => Launch(item), CancellationToken.None);
    }

    private DesktopItemLaunchResult Launch(DesktopItem item)
    {
        if (item.Target is null)
        {
            _logger.LogWarning("The desktop item {Id} has nothing to open", item.Id);
            return DesktopItemLaunchResult.Failed("The item has no target.");
        }

        if (item.IsMissing())
        {
            // The target went away after the item was created. The item stays on the desktop and is
            // marked missing; opening it again reports the same thing.
            _logger.LogWarning("The desktop item {Id} points at {Location}, which is gone", item.Id, item.Location);
            return DesktopItemLaunchResult.Missing(item.Location);
        }

        var plan = DesktopItemLaunch.Plan(item);
        if (plan is null)
        {
            _logger.LogWarning("The desktop item {Id} has nothing to open", item.Id);
            return DesktopItemLaunchResult.Failed("The item has no target.");
        }

        try
        {
            // UseShellExecute is the shell's own "open" — the same path a double-click in Explorer
            // takes — and it is the only way to honour per-user associations.
            Process.Start(new ProcessStartInfo(plan.File) { UseShellExecute = true, Verb = plan.Verb })?.Dispose();
            _logger.LogInformation(
                "The desktop item {Id} ({Name}) opened {Location}",
                item.Id,
                item.Name,
                plan.File);
            return DesktopItemLaunchResult.Launched;
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "The desktop item {Id} could not open {Location}", item.Id, plan.File);
            return DesktopItemLaunchResult.Failed(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "The desktop item {Id} could not open {Location}", item.Id, plan.File);
            return DesktopItemLaunchResult.Failed(ex.Message);
        }
    }
}
