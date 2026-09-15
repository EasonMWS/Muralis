using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;

namespace Muralis.Core.Tests.TestSupport;

/// <summary>
/// Download transport whose behaviour is scripted per attempt, so queue tests can force
/// transient failures, final failures and cancellations deterministically. Scripted
/// successes go through <see cref="SucceedAsync"/> so a real file exists on disk, exactly
/// like the production transport leaves behind.
/// </summary>
internal sealed class FakeDownloadService : IDownloadService
{
    private string? _lastTargetDirectory;
    private string _lastBaseFileName = "wallpaper";

    /// <summary>
    /// Scripted behaviour per attempt (1-based attempt number, plus the token the queue
    /// passed in). Left unset, the download succeeds and writes a real file.
    /// </summary>
    public Func<int, CancellationToken, Task<string>>? OnDownload { get; set; }

    public int Attempts { get; private set; }

    public List<string> Urls { get; } = [];

    public Task<string> DownloadAsync(
        string url,
        string targetDirectory,
        string baseFileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Attempts++;
        Urls.Add(url);
        _lastTargetDirectory = targetDirectory;
        _lastBaseFileName = baseFileName;
        progress?.Report(0.5);
        return OnDownload is { } script ? script(Attempts, cancellationToken) : SucceedAsync();
    }

    /// <summary>Completes like a real download: writes the file into the requested folder.</summary>
    public Task<string> SucceedAsync() => Task.FromResult(CreateCompletedFile());

    /// <summary>Fails like a real transport would.</summary>
    public Task<string> FailAsync() => Task.FromException<string>(new HttpRequestException("offline"));

    /// <summary>Writes the file a completed download would have left behind and returns its path.</summary>
    public string CreateCompletedFile()
    {
        var directory = _lastTargetDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, FileNameHelper.SanitizeFileName(_lastBaseFileName) + ".jpg");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }
}
