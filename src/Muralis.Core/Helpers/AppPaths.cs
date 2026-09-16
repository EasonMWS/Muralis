namespace Muralis.Core.Helpers;

/// <summary>Well-known folders used by the application.</summary>
public static class AppPaths
{
    public const string AppName = "Muralis";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    /// <summary>Optional per-user provider credentials (API keys). Never part of the repository.</summary>
    public static string ProvidersFile => Path.Combine(RootDirectory, "providers.json");

    public static string DatabaseFile => Path.Combine(RootDirectory, "muralis.db");

    /// <summary>Desktop documents (the layout today, per-display profiles later).</summary>
    public static string DesktopDirectory => Path.Combine(RootDirectory, "desktop");

    /// <summary>
    /// The desktop layout: the items on the desktop and the parameters that shape it. Deleting the
    /// file starts from an empty desktop; the rest of the app's configuration is untouched.
    /// </summary>
    public static string DesktopLayoutFile => Path.Combine(DesktopDirectory, "layout.json");

    /// <summary>
    /// The Phase 2 prototype layout. Read once to bring its parameters forward, then kept beside the
    /// current document as <c>.v1.bak</c> — nothing reads it after that.
    /// </summary>
    public static string CanvasPrototypeFile => Path.Combine(RootDirectory, "desktop-canvas-prototype.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string CacheDirectory => Path.Combine(RootDirectory, "cache");

    public static string ThumbnailsDirectory => Path.Combine(CacheDirectory, "thumbnails");

    /// <summary>Cached provider responses (search results, feeds) with a freshness window.</summary>
    public static string MetadataCacheDirectory => Path.Combine(CacheDirectory, "metadata");

    public static string DefaultDownloadFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);

    /// <summary>Creates the folders the application writes to on first run.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
