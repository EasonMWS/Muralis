using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.DockShell;

namespace Muralis.Desktop.Shelf;

/// <summary>
/// Projects the real user and public desktop folders into a stable, read-only Shelf model. File
/// system notifications are coalesced; enumeration never runs on the UI thread.
/// </summary>
public sealed class DesktopShelfService : IDesktopShelfService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private readonly DesktopContentScanner _scanner;
    private readonly ILogger<DesktopShelfService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _watcherGate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounce;
    private bool _started;
    private bool _disposed;

    public DesktopShelfService(DesktopContentScanner scanner, ILogger<DesktopShelfService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public DesktopShelfSnapshot Snapshot { get; private set; } = DesktopShelfSnapshot.Empty;

    public event EventHandler<DesktopShelfSnapshot>? Changed;

    public async Task<DesktopShelfSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            CreateWatchers();
            _started = true;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesktopShelfSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var scan = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            var userFolder = _scanner.Folders.FirstOrDefault();
            var publicFolder = _scanner.Folders.Skip(1).FirstOrDefault();

            var items = scan.Adoptable
                .Select(entry => ToShelfItem(entry, userFolder, publicFolder))
                .OrderBy(item => SortGroup(item.ItemType))
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var snapshot = new DesktopShelfSnapshot(
                items,
                items.Count(item => item.Source == DesktopShelfItemSource.User),
                items.Count(item => item.Source == DesktopShelfItemSource.Public),
                DateTimeOffset.Now,
                Stopwatch.GetElapsedTime(started),
                scan.UnreadableFolders);

            Snapshot = snapshot;
            _logger.LogInformation(
                "Desktop Shelf refreshed in {ElapsedMs:0.0} ms with {Count} items ({User} user, {Public} public)",
                snapshot.EnumerationDuration.TotalMilliseconds,
                snapshot.Items.Count,
                snapshot.UserCount,
                snapshot.PublicCount);
            Changed?.Invoke(this, snapshot);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task OpenAsync(DesktopShelfItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                throw new FileNotFoundException("The desktop item is no longer available.", item.Path);
            }

            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true, Verb = "open" })?.Dispose();
        }, cancellationToken);
    }

    private static DesktopShelfItem ToShelfItem(DesktopContentEntry entry, string? user, string? common)
    {
        var source = IsInside(entry.SourcePath, user)
            ? DesktopShelfItemSource.User
            : IsInside(entry.SourcePath, common)
                ? DesktopShelfItemSource.Public
                : DesktopShelfItemSource.Shell;
        var type = entry.Target switch
        {
            FolderTarget => DockShellItemType.Folder,
            ShortcutTarget => DockShellItemType.Shortcut,
            ApplicationTarget => DockShellItemType.Shortcut,
            _ => DockShellItemType.File,
        };
        var identity = Path.GetFullPath(entry.SourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return new DesktopShelfItem(identity, entry.Name, entry.SourcePath, type, source);
    }

    private static bool IsInside(string path, string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static int SortGroup(DockShellItemType type) => type switch
    {
        DockShellItemType.Folder => 0,
        DockShellItemType.Shortcut or DockShellItemType.Application => 1,
        _ => 2,
    };

    private void CreateWatchers()
    {
        foreach (var folder in _scanner.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                    EnableRaisingEvents = true,
                };
                watcher.Created += OnDesktopChanged;
                watcher.Deleted += OnDesktopChanged;
                watcher.Renamed += OnDesktopChanged;
                watcher.Changed += OnDesktopChanged;
                watcher.Error += OnWatcherError;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The desktop folder {Folder} could not be watched", folder);
            }
        }
    }

    private void OnDesktopChanged(object sender, FileSystemEventArgs args)
    {
        lock (_watcherGate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce ??= new Timer(_ => _ = RefreshAfterChangeAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _logger.LogWarning(args.GetException(), "A Desktop Shelf watcher lost events; scheduling a full refresh");
        OnDesktopChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.All, string.Empty, string.Empty));
    }

    private async Task RefreshAfterChangeAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Desktop Shelf refresh after a file-system event failed");
        }
    }

    public void Dispose()
    {
        lock (_watcherGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce?.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _refreshGate.Dispose();
    }
}
