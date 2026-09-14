namespace Muralis.Core.Models;

/// <summary>
/// Which wallpaper sources the user has switched on, and which one feeds the Home page.
/// Providers themselves stay stateless; this state lives in settings and is applied by
/// the provider manager.
/// </summary>
public sealed class ProviderSettings
{
    /// <summary>Id of the provider Home takes its featured wallpapers from.</summary>
    public string DefaultProviderId { get; set; } = "bing";

    /// <summary>Ids the user has switched off. Providers not listed here are enabled.</summary>
    public List<string> DisabledProviders { get; set; } = [];
}
