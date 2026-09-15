using System.Globalization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;

namespace Muralis.Core.Services;

/// <summary>
/// File-backed metadata cache. Each entry is one file: the expiry timestamp on the first
/// line, the payload after it. Expired entries are reported as missing and deleted lazily.
/// </summary>
public sealed class MetadataCache : IMetadataCache
{
    /// <summary>How many writes happen between sweeps of expired entries.</summary>
    private const int WritesBetweenSweeps = 16;

    private readonly ILogger<MetadataCache> _logger;
    private readonly string _directory;
    private int _writesSinceSweep;

    public MetadataCache(ILogger<MetadataCache> logger, string? directory = null)
    {
        _logger = logger;
        _directory = directory ?? AppPaths.MetadataCacheDirectory;
    }

    public async Task<string?> TryGetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetEntryPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var separator = text.IndexOf('\n');
            if (separator <= 0)
            {
                return null;
            }

            var expiresAt = long.Parse(text[..separator], CultureInfo.InvariantCulture);
            if (DateTimeOffset.UtcNow.UtcTicks >= expiresAt)
            {
                TryDelete(path);
                return null;
            }

            return text[(separator + 1)..];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogDebug(ex, "Ignoring unreadable cache entry {Key}", key);
            TryDelete(path);
            return null;
        }
    }

    public async Task SetAsync(string key, string payload, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = GetEntryPath(key);
            var expiresAt = DateTimeOffset.UtcNow.Add(timeToLive);
            var text = expiresAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" + payload;

            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, text, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);

            if (Interlocked.Increment(ref _writesSinceSweep) >= WritesBetweenSweeps)
            {
                Interlocked.Exchange(ref _writesSinceSweep, 0);
                PruneExpiredEntries();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the cache entry {Key}", key);
        }
    }

    private string GetEntryPath(string key) =>
        Path.Combine(_directory, HashKey(key) + ".cache");

    /// <summary>
    /// Deletes entries whose freshness window has passed. Runs occasionally (not on the
    /// startup path) so keys that are never requested again cannot accumulate on disk.
    /// </summary>
    private void PruneExpiredEntries()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var removed = 0;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.cache"))
            {
                if (IsExpired(path))
                {
                    TryDelete(path);
                    removed++;
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} expired metadata cache entries", removed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not sweep the metadata cache");
        }
    }

    private static bool IsExpired(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            var firstLine = reader.ReadLine();
            return long.TryParse(firstLine, CultureInfo.InvariantCulture, out var expiresAt)
                && DateTimeOffset.UtcNow.UtcTicks >= expiresAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string HashKey(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the stale cache file {Path}", path);
        }
    }
}
