using Muralis.Core.Helpers;

namespace Muralis.Core.Models;

/// <summary>A display that wallpapers can be applied to.</summary>
public sealed class MonitorInfo
{
    /// <summary>Platform-specific stable id (on Windows: the device path used by <c>IDesktopWallpaper</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Friendly name for the UI, e.g. "Display 1 (Primary)".</summary>
    public string DisplayName { get; init; } = string.Empty;

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool IsPrimary { get; init; }

    public string ResolutionText => DisplayFormat.Resolution(Width, Height);

    public string SummaryText => $"{DisplayName} — {ResolutionText}";
}
