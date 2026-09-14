using System.Reflection;

namespace Muralis.App.Infrastructure;

/// <summary>Single source of truth for product metadata shown in the UI.</summary>
public static class AppInfo
{
    public const string DisplayName = "Muralis";

    public const string Tagline = "Wallpaper Studio";

    public const string GitHubUrl = "https://github.com/muralis/muralis";

    public const string LicenseName = "MIT License";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.1.0";
}
