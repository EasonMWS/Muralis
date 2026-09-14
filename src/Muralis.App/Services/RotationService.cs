using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.Services;

/// <summary>
/// Applies wallpapers on a schedule. Runs while the app is running; the schedule and
/// source come from <see cref="RotationSettings"/> and changes apply immediately.
/// </summary>
public sealed class RotationService : IDisposable
{
    private static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".avif"];

    private readonly ISettingsService _settingsService;
    private readonly IWallpaperService _wallpaperService;
    private readonly ILocalLibrary _library;
    private readonly ILogger<RotationService> _logger;
    private readonly Random _random = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private RotationSettings _activeConfig = new();
    private string? _lastAppliedId;

    public RotationService(
        ISettingsService settingsService,
        IWallpaperService wallpaperService,
        ILocalLibrary library,
        ILogger<RotationService> logger)
    {
        _settingsService = settingsService;
        _wallpaperService = wallpaperService;
        _library = library;
        _logger = logger;

        _settingsService.SettingsChanged += (_, _) => ApplySettings();
    }

    /// <summary>Starts, stops or restarts the timer to match the current settings.</summary>
    public void ApplySettings()
    {
        var rotation = _settingsService.Current.Rotation;
        if (SameConfig(_activeConfig, rotation) && _loopCts is not null == rotation.Enabled)
        {
            return;
        }

        _activeConfig = Clone(rotation);
        StopLoop();

        if (!rotation.Enabled)
        {
            _logger.LogInformation("Auto rotation is disabled");
            return;
        }

        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(rotation.Interval.ToTimeSpan(), _loopCts.Token);
    }

    /// <summary>Applies the next wallpaper immediately (also used by the tray menu).</summary>
    public async Task<Wallpaper?> ApplyNextAsync(CancellationToken cancellationToken = default)
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = await ResolveCandidatesAsync(cancellationToken).ConfigureAwait(false);
            var next = RotationPlanner.PickNext(candidates, _lastAppliedId, _random);
            if (next?.LocalPath is not { Length: > 0 } path)
            {
                _logger.LogWarning("Rotation has no usable wallpapers (source: {Source})",
                    _settingsService.Current.Rotation.UseFavorites ? "favorites" : "folder");
                return null;
            }

            await _wallpaperService
                .SetWallpaperAsync(path, _settingsService.Current.DefaultFitMode, monitorId: null, cancellationToken)
                .ConfigureAwait(false);

            _lastAppliedId = next.Id;
            if (_library.Find(next.Id) is not null)
            {
                await _library.RecordUsageAsync(next, monitorName: null, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Rotation applied: {Title}", next.Title);
            return next;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rotation could not apply the next wallpaper");
            return null;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<IReadOnlyList<Wallpaper>> ResolveCandidatesAsync(CancellationToken cancellationToken)
    {
        var rotation = _settingsService.Current.Rotation;

        if (rotation.UseFavorites)
        {
            return _library.Favorites
                .Where(item => item.HasLocalFile && File.Exists(item.LocalPath))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(rotation.SourceFolder) || !Directory.Exists(rotation.SourceFolder))
        {
            return [];
        }

        return await Task.Run(() => ScanFolder(rotation.SourceFolder!), cancellationToken).ConfigureAwait(false);
    }

    private static List<Wallpaper> ScanFolder(string folder)
    {
        var results = new List<Wallpaper>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new Wallpaper
                {
                    Id = WallpaperId.ForLocalFile(file),
                    Title = FileNameHelper.ToTitle(file),
                    LocalPath = file,
                    Source = WallpaperSource.Local,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: return whatever was collected.
        }

        return results;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Auto rotation started (every {Interval})", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ApplyNextAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or reconfigured.
        }
        finally
        {
            _logger.LogInformation("Auto rotation timer stopped");
        }
    }

    private void StopLoop()
    {
        var cts = _loopCts;
        _loopCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private static bool SameConfig(RotationSettings left, RotationSettings right) =>
        left.Enabled == right.Enabled
        && left.Interval == right.Interval
        && left.Shuffle == right.Shuffle
        && left.UseFavorites == right.UseFavorites
        && string.Equals(left.SourceFolder, right.SourceFolder, StringComparison.OrdinalIgnoreCase);

    private static RotationSettings Clone(RotationSettings source) => new()
    {
        Enabled = source.Enabled,
        Interval = source.Interval,
        Shuffle = source.Shuffle,
        UseFavorites = source.UseFavorites,
        SourceFolder = source.SourceFolder,
    };

    public void Dispose()
    {
        StopLoop();
        _applyLock.Dispose();
    }
}
