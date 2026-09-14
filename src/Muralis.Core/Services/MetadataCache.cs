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
    private readonly ILogger<MetadataCache> _logger;
    private readonly string _directory;

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
