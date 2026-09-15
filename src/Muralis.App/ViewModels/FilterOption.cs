using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Services;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

/// <summary>
/// An orientation choice in the Browse filter flyout. Names follow the display language
/// without rebuilding the picker.
/// </summary>
public sealed class OrientationOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public OrientationOption(WallpaperOrientation value, string resourceKey, ILocalizationService localization)
    {
        Value = value;
        ResourceKey = resourceKey;
        _localization = localization;
    }

    public WallpaperOrientation Value { get; }

    public string ResourceKey { get; }

    public string Name => _localization.Get(ResourceKey);

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}

/// <summary>A minimum-resolution choice in the Browse filter flyout.</summary>
public sealed class ResolutionOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public ResolutionOption(WallpaperResolution value, string resourceKey, ILocalizationService localization)
    {
        Value = value;
        ResourceKey = resourceKey;
        _localization = localization;
    }

    public WallpaperResolution Value { get; }

    public string ResourceKey { get; }

    public string Name => _localization.Get(ResourceKey);

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}
