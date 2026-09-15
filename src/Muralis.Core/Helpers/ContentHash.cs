using System.Security.Cryptography;

namespace Muralis.Core.Helpers;

/// <summary>
/// Content fingerprint of an image file, used to spot the same picture arriving
/// twice (under a different name, folder or source).
/// </summary>
public static class ContentHash
{
    /// <summary>SHA-256 of the file content as lowercase hex, or null when the file cannot be read.</summary>
    public static async Task<string?> TryComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
