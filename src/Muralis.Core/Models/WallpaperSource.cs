namespace Muralis.Core.Models;

public enum WallpaperSource
{
    /// <summary>The image file exists on this machine.</summary>
    Local,

    /// <summary>The image lives on a remote provider and may need to be downloaded.</summary>
    Online,
}
