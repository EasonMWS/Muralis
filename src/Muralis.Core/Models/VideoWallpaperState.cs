namespace Muralis.Core.Models;

/// <summary>Lifecycle of the video that plays behind the desktop icons.</summary>
public enum VideoWallpaperState
{
    /// <summary>No video on the desktop; the static background is showing.</summary>
    Stopped,

    /// <summary>A video is being opened and its first frame has not been presented yet.</summary>
    Starting,

    /// <summary>The video is playing.</summary>
    Playing,

    /// <summary>The last attempt failed; see <see cref="VideoWallpaperStatus.Error"/>.</summary>
    Failed,
}
