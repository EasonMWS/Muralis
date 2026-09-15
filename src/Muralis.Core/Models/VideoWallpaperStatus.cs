namespace Muralis.Core.Models;

/// <summary>What the video wallpaper is doing right now.</summary>
/// <param name="State">Current lifecycle stage.</param>
/// <param name="VideoPath">File being played, when one is loaded.</param>
/// <param name="Error">Human-readable reason when <paramref name="State"/> is <see cref="VideoWallpaperState.Failed"/>.</param>
public sealed record VideoWallpaperStatus(VideoWallpaperState State, string? VideoPath = null, string? Error = null)
{
    public static VideoWallpaperStatus Stopped { get; } = new(VideoWallpaperState.Stopped);
}
