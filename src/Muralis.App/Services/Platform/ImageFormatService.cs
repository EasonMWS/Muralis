using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Muralis.Core.Helpers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Muralis.App.Services.Platform;

public interface IImageFormatService
{
    /// <summary>
    /// Returns a path Windows can use as a desktop background. Formats such as webp
    /// or avif are transcoded to JPEG inside the app cache; everything else is
    /// returned unchanged.
    /// </summary>
    Task<string> EnsureSupportedFormatAsync(string imagePath, CancellationToken cancellationToken = default);
}

public sealed class ImageFormatService : IImageFormatService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly ILogger<ImageFormatService> _logger;

    public ImageFormatService(ILogger<ImageFormatService> logger) => _logger = logger;

    public async Task<string> EnsureSupportedFormatAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (SupportedExtensions.Contains(Path.GetExtension(imagePath)))
        {
            return imagePath;
        }

        var convertedDirectory = Path.Combine(AppPaths.CacheDirectory, "converted");
        Directory.CreateDirectory(convertedDirectory);
        var targetPath = Path.Combine(convertedDirectory, ShortHash(imagePath) + ".jpg");
        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        _logger.LogInformation("Transcoding {Source} to JPEG for desktop use", imagePath);

        try
        {
            using var sourceStream = File.OpenRead(imagePath).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(sourceStream).AsTask(cancellationToken).ConfigureAwait(false);

            using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

            using var destinationStream = File.Create(targetPath).AsRandomAccessStream();
            var encoder = await BitmapEncoder
                .CreateAsync(BitmapEncoder.JpegEncoderId, destinationStream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            encoder.SetSoftwareBitmap(converted);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

            return targetPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not transcode {Path}; the system may lack a codec for this format", imagePath);
            throw new NotSupportedException(
                "This image format is not supported on your system. Try a JPG, PNG or BMP file.", ex);
        }
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..16].ToLowerInvariant();
}
