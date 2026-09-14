namespace Muralis.Core.Helpers;

/// <summary>Well-known folders used by the application.</summary>
public static class AppPaths
{
    public const string AppName = "Muralis";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string CacheDirectory => Path.Combine(RootDirectory, "cache");

    public static string ThumbnailsDirectory => Path.Combine(CacheDirectory, "thumbnails");

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
