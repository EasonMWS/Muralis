using Muralis.Core.Helpers;

namespace Muralis.Core.Models;

/// <summary>
/// Represents a single wallpaper, whether it lives on disk, online, or both.
/// </summary>
public sealed class Wallpaper
{
    /// <summary>Stable identity, e.g. <c>local:{sha256-of-path}</c> or <c>bing:2026-09-14</c>.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Absolute path of the image file on this machine, if available.</summary>
    public string? LocalPath { get; set; }

    public string? RemoteUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long FileSize { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = [];

    public bool IsFavorite { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastUsedAt { get; set; }

    public WallpaperSource Source { get; set; } = WallpaperSource.Local;

    public double AspectRatio => Height > 0 ? (double)Width / Height : 0d;

    public bool HasLocalFile => !string.IsNullOrEmpty(LocalPath);

    public string ResolutionText => DisplayFormat.Resolution(Width, Height);

    public string AspectRatioText => DisplayFormat.AspectRatio(Width, Height);

    public string FileSizeText => FileSize > 0 ? DisplayFormat.FileSize(FileSize) : string.Empty;
}
