using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App.ViewModels;

/// <summary>Localized names for wallpaper sources, resolved from the provider id.</summary>
internal static class ProviderDisplay
{
    public static string Name(ILocalizationService localization, IWallpaperProvider provider) =>
        localization.GetOrFallback($"Provider_{provider.Id}", provider.DisplayName);

    public static string Description(ILocalizationService localization, IWallpaperProvider provider) =>
        localization.GetOrFallback($"Provider_Description_{provider.Id}", string.Empty);
}
