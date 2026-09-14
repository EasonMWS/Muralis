using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App.ViewModels;

/// <summary>
/// An entry in the browse source picker: a single provider, or the "all sources" entry
/// (<see cref="Provider"/> is <c>null</c>). Names follow the display language without
/// rebuilding the picker.
/// </summary>
public sealed partial class SourceOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public SourceOption(IWallpaperProvider? provider, ILocalizationService localization)
    {
        Provider = provider;
        _localization = localization;
    }

    public IWallpaperProvider? Provider { get; }

    public bool IsAllSources => Provider is null;

    public string Name => Provider is null
        ? _localization.Get("Browse_AllSources")
        : ProviderDisplay.Name(_localization, Provider);

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}
