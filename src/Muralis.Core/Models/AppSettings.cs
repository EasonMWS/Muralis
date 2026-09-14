namespace Muralis.Core.Models;

/// <summary>
/// User-facing application settings, persisted as JSON in
/// <c>%LOCALAPPDATA%\Muralis\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bumped whenever the on-disk shape changes in a breaking way.</summary>
    public int SchemaVersion { get; set; } = 1;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public WallpaperFitMode DefaultFitMode { get; set; } = WallpaperFitMode.Fill;

    /// <summary>
    /// Folder where downloaded wallpapers are stored. Empty means
    /// <see cref="Helpers.AppPaths.DefaultDownloadFolder"/>.
    /// </summary>
    public string DownloadFolder { get; set; } = string.Empty;

    public bool LaunchAtStartup { get; set; }

    public RotationSettings Rotation { get; set; } = new();
}
