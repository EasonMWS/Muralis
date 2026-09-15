using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>Compares the running version with the newest published release.</summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Queries the releases API and reports whether a newer version exists. Returns null
    /// when no usable release was found (none published yet, or an unreadable tag).
    /// Throws when the network call itself fails.
    /// </summary>
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
}
