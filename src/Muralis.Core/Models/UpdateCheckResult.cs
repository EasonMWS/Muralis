namespace Muralis.Core.Models;

/// <summary>Outcome of comparing the running version with the newest published release.</summary>
/// <param name="IsUpdateAvailable">True when <paramref name="LatestVersion"/> is newer than the running build.</param>
/// <param name="LatestVersion">The release tag with any leading "v" removed, for example "0.2.0".</param>
/// <param name="ReleaseUrl">Page to open for the release; null when the response carried no link.</param>
public sealed record UpdateCheckResult(bool IsUpdateAvailable, string LatestVersion, string? ReleaseUrl);
