using System.Reflection;

namespace Muralis.App.Infrastructure;

/// <summary>Single source of truth for product metadata shown in the UI.</summary>
public static class AppInfo
{
    public const string DisplayName = "Muralis";

    public const string Tagline = "Wallpaper Studio";

    public const string GitHubOwner = "EasonMWS";

    public const string GitHubRepo = "Muralis";

    public const string GitHubUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    public const string GitHubReleasesUrl = $"{GitHubUrl}/releases";

    /// <summary>Endpoint the update check queries for the newest published release.</summary>
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    /// <summary>How to enable sources and configure API keys.</summary>
    public const string ProvidersDocUrl = $"{GitHubUrl}/blob/main/docs/providers.md";

    public const string LicenseName = "MIT License";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.2.0";
}
