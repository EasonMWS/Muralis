namespace Muralis.Core.Models;

/// <summary>Interval between automatic wallpaper changes.</summary>
public enum RotationInterval
{
    Minutes15 = 15,
    Minutes30 = 30,
    Hours1 = 60,
    Hours6 = 360,
    Daily = 1440,
}

public sealed class RotationSettings
{
    public bool Enabled { get; set; }

    public RotationInterval Interval { get; set; } = RotationInterval.Minutes30;

    public bool Shuffle { get; set; } = true;

    /// <summary>Use the favorites collection as the rotation source when true.</summary>
    public bool UseFavorites { get; set; } = true;

    /// <summary>Folder used as the rotation source when <see cref="UseFavorites"/> is false.</summary>
    public string? SourceFolder { get; set; }
}
