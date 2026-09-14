using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App.ViewModels;

/// <summary>
/// Presents a wallpaper provider in the browse source picker. The display name is
/// resolved from the localization resources by provider id, so it follows the language
/// without rebuilding the provider list.
/// </summary>
public sealed partial class ProviderOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public ProviderOption(IWallpaperProvider provider, ILocalizationService localization)
    {
        Provider = provider;
        _localization = localization;
    }

    public IWallpaperProvider Provider { get; }

    public string Name => _localization.GetOrFallback($"Provider_{Provider.Id}", Provider.DisplayName);

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}
