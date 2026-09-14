using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Providers;

/// <summary>
/// Development provider that surfaces the sample images shipped with Windows so the
/// UI has realistic content before real online providers are wired up.
/// </summary>
public sealed class MockWallpaperProvider : IWallpaperProvider
{
    private static readonly string[] SampleRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Screen"),
    ];

    private readonly ILogger<MockWallpaperProvider> _logger;
    private readonly Lock _loadLock = new();
    private readonly List<Wallpaper> _cache = [];
    private bool _loaded;

    public MockWallpaperProvider(ILogger<MockWallpaperProvider> logger) => _logger = logger;

    public string Id => "mock";

    public string DisplayName => "Sample wallpapers";

    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();

        IEnumerable<Wallpaper> items = _cache;
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            items = items.Where(w =>
                w.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var page = items
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        // A small delay keeps loading states honest during development.
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        return page;
    }

    public Task<Wallpaper?> GetWallpaperAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        Wallpaper? match = _cache.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    private void EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_loaded)
            {
                return;
            }

            foreach (var root in SampleRoots.Where(Directory.Exists))
            {
                foreach (var file in EnumerateImages(root))
                {
                    ImageMetadataReader.TryReadDimensions(file, out var width, out var height);
                    _cache.Add(new Wallpaper
                    {
                        Id = WallpaperId.ForLocalFile(file),
                        Title = FileNameHelper.ToTitle(file),
                        LocalPath = file,
                        Width = width,
                        Height = height,
                        FileSize = TryGetFileSize(file),
                        Tags = ["Sample", "Windows"],
                        Source = WallpaperSource.Local,
                    });
                }
            }

            _loaded = true;
            _logger.LogInformation("Mock provider loaded {Count} sample wallpapers", _cache.Count);
        }
    }

    private static IEnumerable<string> EnumerateImages(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static long TryGetFileSize(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

}
