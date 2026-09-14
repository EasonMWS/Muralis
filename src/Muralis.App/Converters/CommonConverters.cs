using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Muralis.App.Converters;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
}

/// <summary>Collapses null values, empty strings and empty collections.</summary>
public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            System.Collections.ICollection collection => collection.Count > 0,
            _ => true,
        };

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Loads a local image file into a downscaled <see cref="BitmapImage"/> so grid
/// thumbnails never decode at full resolution. Decoded bitmaps are kept in a
/// least-recently-used cache with a hard entry limit: a grid of a few hundred
/// wallpapers must not pin hundreds of megabytes of pixels.
/// </summary>
public sealed partial class LocalPathToImageSourceConverter : IValueConverter
{
    private const int DefaultDecodeWidth = 512;

    /// <summary>
    /// Roughly two screenfuls of cards. Bitmaps decode at the logical size times the
    /// display scale, so a small bound keeps a long browsing session from pinning tens of
    /// megabytes; evicted entries re-decode on demand from local files.
    /// </summary>
    private const int MaxCachedBitmaps = 24;

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> CacheOrder = new();

    private sealed record CacheEntry(string Key, BitmapImage Bitmap);

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var decodeWidth = parameter is string text && int.TryParse(text, out var parsed)
            ? parsed
            : DefaultDecodeWidth;

        var key = $"{path}|{decodeWidth}|{SafeLastWriteTime(path)}";
        return GetOrAdd(key, path, decodeWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static BitmapImage GetOrAdd(string key, string path, int decodeWidth)
    {
        lock (CacheLock)
        {
            if (CacheIndex.TryGetValue(key, out var existing))
            {
                CacheOrder.Remove(existing);
                CacheOrder.AddFirst(existing);
                return existing.Value.Bitmap;
            }
        }

        // Decode outside the lock; a duplicate decode of the same key is harmless.
        var bitmap = CreateBitmap(path, decodeWidth);

        lock (CacheLock)
        {
            if (CacheIndex.TryGetValue(key, out var raced))
            {
                return raced.Value.Bitmap;
            }

            var node = CacheOrder.AddFirst(new CacheEntry(key, bitmap));
            CacheIndex[key] = node;

            while (CacheOrder.Count > MaxCachedBitmaps && CacheOrder.Last is { } oldest)
            {
                CacheOrder.RemoveLast();
                CacheIndex.Remove(oldest.Value.Key);
                oldest.Value.Bitmap.UriSource = null;
            }
        }

        return bitmap;
    }

    private static BitmapImage CreateBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = decodeWidth,
            DecodePixelType = DecodePixelType.Logical,
        };
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        return bitmap;
    }

    private static long SafeLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Produces a stable, pleasant gradient from an id string. Used as the
/// placeholder behind cards while their image loads (or when there is none).
/// </summary>
public sealed partial class IdToPlaceholderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var seed = value as string ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hue = hash[0] / 255d * 360d;
        var hueShift = 24 + (hash[1] / 255d * 40d);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = FromHsl(hue, 0.34, 0.42), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = FromHsl((hue + hueShift) % 360, 0.38, 0.24), Offset = 1 });
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var secondary = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var offset = lightness - (chroma / 2);

        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, secondary, 0d),
            < 120 => (secondary, chroma, 0d),
            < 180 => (0d, chroma, secondary),
            < 240 => (0d, secondary, chroma),
            < 300 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary),
        };

        return Color.FromArgb(
            255,
            (byte)Math.Round((red + offset) * 255),
            (byte)Math.Round((green + offset) * 255),
            (byte)Math.Round((blue + offset) * 255));
    }
}
