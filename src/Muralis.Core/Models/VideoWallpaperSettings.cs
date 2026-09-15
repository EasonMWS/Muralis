namespace Muralis.Core.Models;

/// <summary>The video the user picked as an animated background, remembered across restarts.</summary>
public sealed class VideoWallpaperSettings
{
    /// <summary>Whether the video is put back on the desktop the next time the app starts.</summary>
    public bool Enabled { get; set; }

    public string VideoPath { get; set; } = string.Empty;

    public bool Muted { get; set; } = true;
}
