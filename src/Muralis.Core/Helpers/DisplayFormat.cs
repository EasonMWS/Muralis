using System.Globalization;

namespace Muralis.Core.Helpers;

/// <summary>Formatting helpers shared by views and view models.</summary>
public static class DisplayFormat
{
    /// <summary>Formats a resolution like "3840 × 2400"; empty when the size is not known yet.</summary>
    public static string Resolution(int width, int height) =>
        width > 0 && height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{width} × {height}")
            : string.Empty;

    public static string AspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return string.Empty;
        }

        var divisor = GreatestCommonDivisor(width, height);
        var ratioWidth = width / divisor;
        var ratioHeight = height / divisor;

        return ratioWidth <= 50 && ratioHeight <= 50
            ? string.Create(CultureInfo.InvariantCulture, $"{ratioWidth}:{ratioHeight}")
            : string.Create(CultureInfo.InvariantCulture, $"{(double)width / height:F2}:1");
    }

    public static string FileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.#";
        return string.Create(CultureInfo.InvariantCulture, $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}");
    }

    public static string RelativeTime(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var delta = (now ?? DateTimeOffset.Now) - value;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes} min ago");
        }

        if (delta.TotalHours < 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours} h ago");
        }

        if (delta.TotalDays < 30)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalDays} d ago");
        }

        return value.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Abs(a);
    }
}
