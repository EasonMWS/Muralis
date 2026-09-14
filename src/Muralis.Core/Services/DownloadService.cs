using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;

namespace Muralis.Core.Services;

public sealed class DownloadService : IDownloadService
{
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(HttpClient httpClient, ILogger<DownloadService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadAsync(
        string url,
        string targetDirectory,
        string baseFileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        Directory.CreateDirectory(targetDirectory);

        var extension = GetExtensionFromUrl(url);
        var fileName = FileNameHelper.SanitizeFileName(baseFileName) + extension;
        var targetPath = FileNameHelper.EnsureUniqueFilePath(Path.Combine(targetDirectory, fileName));

        // Download to a temporary file first so a failed transfer never leaves a partial wallpaper.
        var temporaryPath = targetPath + ".download";
        try
        {
            _logger.LogInformation("Downloading {Url} to {Path}", url, targetPath);

            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (totalBytes is > 0)
                    {
                        progress?.Report(Math.Min(1d, (double)received / totalBytes.Value));
                    }
                }

                if (totalBytes is null or 0)
                {
                    progress?.Report(1d);
                }
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
            _logger.LogInformation("Download finished: {Path}", targetPath);
            return targetPath;
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the partial download {Path}", path);
        }
    }

    private static string GetExtensionFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(extension) && extension.Length <= 5)
            {
                return extension;
            }
        }

        return ".jpg";
    }
}
