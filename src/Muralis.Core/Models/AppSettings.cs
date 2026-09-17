namespace Muralis.Core.Models;

/// <summary>
/// User-facing application settings, persisted as JSON in
/// <c>%LOCALAPPDATA%\Muralis\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    /// <summary>Bumped whenever the on-disk shape changes in a breaking way.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Display language as a BCP-47 code (e.g. <c>en-US</c>, <c>zh-CN</c>).
    /// Empty means "follow the system language".
    /// </summary>
    public string Language { get; set; } = string.Empty;

    public WallpaperFitMode DefaultFitMode { get; set; } = WallpaperFitMode.Fill;

    /// <summary>Which wallpaper sources are switched on, and which one feeds the Home page.</summary>
    public ProviderSettings Providers { get; set; } = new();

    /// <summary>
    /// Folder where downloaded wallpapers are stored. Empty means
    /// <see cref="Helpers.AppPaths.DefaultDownloadFolder"/>.
    /// </summary>
    public string DownloadFolder { get; set; } = string.Empty;

    public bool LaunchAtStartup { get; set; }

    /// <summary>Keep the app (and rotation) alive in the tray when the window is closed.</summary>
    public bool CloseToTray { get; set; } = true;

    public RotationSettings Rotation { get; set; } = new();

    /// <summary>The video shown behind the desktop icons, and whether to bring it back on launch.</summary>
    public VideoWallpaperSettings VideoWallpaper { get; set; } = new();

    /// <summary>
    /// The product-level desktop experience. This deliberately lives outside the Phase 3 canvas
    /// document: Native and Muralis Mode do not belong to the legacy takeover layout.
    /// </summary>
    public DesktopExperienceSettings DesktopExperience { get; set; } = new();

    /// <summary>
    /// The dock and the applications pinned to it. Like the desktop experience it is its own section,
    /// because the dock is useful on a fully native desktop and belongs to no takeover document.
    /// </summary>
    public DockSettings Dock { get; set; } = new();
}
