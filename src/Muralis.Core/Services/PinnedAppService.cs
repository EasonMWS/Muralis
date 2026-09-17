using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Diagnostics;
using Muralis.Core.DockShell;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// The pinned applications, held as one list and written to settings whenever the user changes it.
/// It owns no window and no shell call of its own: what a path describes comes from the inspector,
/// what a launch does from the launcher, and what survives a restart from the settings file.
/// </summary>
/// <remarks>
/// <para>
/// The list is only ever changed by pinning, unpinning and reordering, and each of those is followed
/// by exactly one save — never by a save per pointer move, which is what a drag would produce if the
/// order were persisted while the item was still being carried.
/// </para>
/// <para>
/// Nothing here can fail the app: a settings section that cannot be read leaves an empty dock, an
/// application that cannot be described is refused in the caller's words, and a pin whose target has
/// been deleted stays in the list and reports itself unavailable.
/// </para>
/// </remarks>
public sealed class PinnedAppService : IPinnedAppService
{
    private readonly ISettingsService _settings;
    private readonly IApplicationInspector _inspector;
    private readonly IApplicationLauncher _launcher;
    private readonly ILogger<PinnedAppService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private IReadOnlyList<PinnedApp> _items = [];

    public PinnedAppService(
        ISettingsService settings,
        IApplicationInspector inspector,
        IApplicationLauncher launcher,
        ILogger<PinnedAppService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<PinnedApp> Items => _items;

    /// <inheritdoc />
    public int MaximumCount => PinnedApps.MaximumCount;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<PinnedApp>>? Changed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PinnedApp>> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = _settings.Current.Dock.PinnedApps;
            var items = new List<PinnedApp>(saved.Count);
            var refused = 0;
            foreach (var entry in saved)
            {
                if (entry is not null && entry.TryToPinnedApp(out var app) && app is not null)
                {
                    items.Add(app);
                }
                else
                {
                    refused++;
                }
            }

            if (refused > 0)
            {
                // One unreadable entry is not a reason to lose the rest of the dock, and it is not
                // repaired into a pin the user never made either.
                _logger.LogWarning("{Refused} saved pinned app(s) could not be read and were left out of the dock", refused);
            }

            _items = items;
            _logger.LogInformation("The dock restored {Count} pinned app(s)", items.Count);
            Publish();
            return _items;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PinnedAppAddResult> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PinnedAppAddResult.Unsupported("No file was chosen.");
        }

        if (!PinnedAppTargets.IsSupported(path))
        {
            return PinnedAppAddResult.Unsupported(
                $"'{Path.GetFileName(path)}' is not an application. The dock pins programs and shortcuts.");
        }

        ApplicationDescription? described;
        try
        {
            described = await _inspector.DescribeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The chosen application {Path} could not be read", path);
            return PinnedAppAddResult.Unsupported(ex.Message);
        }

        if (described is null)
        {
            return PinnedAppAddResult.Unsupported($"'{Path.GetFileName(path)}' could not be read as an application.");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(described.LaunchTarget))
            {
                return PinnedAppAddResult.Unsupported($"'{Path.GetFileName(described.LaunchTarget)}' is no longer there.");
            }

            var candidate = new PinnedApp(
                Guid.NewGuid().ToString("n"),
                described.DisplayName,
                described.LaunchTarget,

                // The shell is asked for the icon of the file the user picked, so a shortcut shows the
                // picture the shortcut itself carries rather than an invented one.
                described.LaunchTarget,
                described.Kind,
                described.Identity);

            var result = PinnedApps.Add(_items, candidate, MaximumCount);
            if (!result.Succeeded)
            {
                _logger.LogInformation(
                    "The application {Name} was not pinned again: {Outcome}",
                    described.DisplayName,
                    result.Outcome);
                return result;
            }

            _items = [.. _items, candidate];
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "The application {Name} was pinned from {Target} ({Kind})",
                candidate.DisplayName,
                candidate.LaunchTarget,
                candidate.Kind);
            Publish();
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = PinnedApps.Remove(_items, id);
            if (remaining.Count == _items.Count)
            {
                return false;
            }

            var removed = PinnedApps.Find(_items, id);
            _items = remaining;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "The application {Name} was unpinned; the file it points at was not touched",
                removed?.DisplayName ?? id);
            Publish();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PinnedApp>> MoveAsync(
        string id,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        var waiting = DropProfile.Measure("commit.mutex");
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        waiting.Dispose();

        try
        {
            IReadOnlyList<PinnedApp> moved;
            bool unchanged;
            using (DropProfile.Measure("commit.model"))
            {
                moved = PinnedApps.Move(_items, id, targetIndex);
                unchanged = SameOrder(moved, _items);
            }

            if (unchanged)
            {
                return _items;
            }

            _items = moved;
            await PersistAsync(cancellationToken).ConfigureAwait(false);

            using (DropProfile.Measure("commit.log"))
            {
                _logger.LogInformation(
                    "The pinned application {Name} was moved to position {Position}",
                    PinnedApps.Find(_items, id)?.DisplayName ?? id,
                    targetIndex);
            }

            using (DropProfile.Measure("publish"))
            {
                Publish();
            }

            return _items;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ApplicationLaunchResult> LaunchAsync(string id, CancellationToken cancellationToken = default)
    {
        var app = PinnedApps.Find(_items, id);
        if (app is null)
        {
            return ApplicationLaunchResult.Failed("That application is not pinned any more.");
        }

        if (!IsAvailable(app))
        {
            _logger.LogWarning("The pinned application {Name} points at {Target}, which is gone", app.DisplayName, app.LaunchTarget);
            return ApplicationLaunchResult.Missing(app.LaunchTarget);
        }

        var result = await _launcher
            .LaunchAsync(new ApplicationLaunchRequest(app.LaunchTarget, app.Arguments, app.WorkingDirectory), cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ApplicationLaunchOutcome.Launched:
                _logger.LogInformation("The pinned application {Name} was started", app.DisplayName);
                break;
            case ApplicationLaunchOutcome.Missing:
                _logger.LogWarning("The pinned application {Name} could not be started: it is gone", app.DisplayName);
                break;
            default:
                _logger.LogError(
                    "The pinned application {Name} could not be started: {Problem}",
                    app.DisplayName,
                    result.Error ?? "the shell refused");
                break;
        }

        return result;
    }

    /// <inheritdoc />
    public bool IsAvailable(PinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Called once per pin every time the zone is redrawn, so it is a file probe per pin on whichever
        // thread asked. Recorded because a drop redraws the whole zone.
        using var probing = DropProfile.Measure("dock.available");
        return File.Exists(app.LaunchTarget);
    }

    /// <summary>
    /// Writes the list where the next launch reads it. The save is awaited rather than left to the
    /// settings service's own background write, so a restart cannot read an order the user has
    /// already changed.
    /// </summary>
    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        List<PinnedAppSettings> snapshot;
        using (DropProfile.Measure("persist.snapshot"))
        {
            snapshot = _items.Select(PinnedAppSettings.From).ToList();
        }

        using (DropProfile.Measure("persist.update"))
        {
            _settings.Update(settings => settings.Dock.PinnedApps = snapshot);
        }

        // This is the write the user's order is waited for. The settings service also writes the change
        // off its own bat, and which of the two a reader is looking at is what "settings.save.scheduled"
        // and this pair of stages are for.
        using (DropProfile.Measure("persist.awaited"))
        {
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SameOrder(IReadOnlyList<PinnedApp> first, IReadOnlyList<PinnedApp> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            if (!string.Equals(first[i].Id, second[i].Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void Publish() => Changed?.Invoke(this, _items);
}
