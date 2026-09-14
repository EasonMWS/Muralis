namespace Muralis.Core.Abstractions;

/// <summary>Downloads remote wallpaper files to disk with progress and conflict-safe names.</summary>
public interface IDownloadService
{
    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="targetDirectory"/> and returns
    /// the full path of the created file. Existing files are never overwritten: a numeric
    /// suffix is added instead. Progress is reported as a value between 0 and 1.
    /// </summary>
    Task<string> DownloadAsync(
        string url,
        string targetDirectory,
        string baseFileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
