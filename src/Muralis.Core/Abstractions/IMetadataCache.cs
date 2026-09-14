namespace Muralis.Core.Abstractions;

/// <summary>
/// Caches raw provider responses on disk so grids and feeds do not hit rate-limited APIs
/// again while the cached data is still fresh. Values are opaque strings (JSON payloads).
/// </summary>
public interface IMetadataCache
{
    /// <summary>Returns the cached payload, or <c>null</c> when missing or expired.</summary>
    Task<string?> TryGetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string payload, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}
