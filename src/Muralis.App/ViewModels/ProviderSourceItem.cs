using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Infrastructure;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

/// <summary>
/// One row in Settings &gt; Wallpaper sources: the localized name and description, the
/// enable switch (persisted through the provider manager) and the API key hint.
/// </summary>
public sealed partial class ProviderSourceItem : ObservableObject
{
    private readonly WallpaperProviderManager _providerManager;
    private readonly ILocalizationService _localization;
    private readonly Action<string, string, bool> _reportStatus;

    public ProviderSourceItem(
        IWallpaperProvider provider,
        WallpaperProviderManager providerManager,
        ILocalizationService localization,
        Action<string, string, bool> reportStatus)
    {
        Provider = provider;
        _providerManager = providerManager;
        _localization = localization;
        _reportStatus = reportStatus;
    }

    public IWallpaperProvider Provider { get; }

    public string Id => Provider.Id;

    public string Name => ProviderDisplay.Name(_localization, Provider);

    public string Description => ProviderDisplay.Description(_localization, Provider);

    public string Glyph => Provider.Id switch
    {
        "bing" => "\uE774",
        "wallhaven" => "\uE8B9",
        "nasa" => "\uE9D9",
        _ => "\uE8F1",
    };

    public ProviderAvailability Availability => _providerManager.GetAvailability(Provider);

    /// <summary>True while the source needs an API key before it can be used.</summary>
    public bool NeedsApiKey => Availability == ProviderAvailability.NeedsApiKey;

    public bool IsEnabled
    {
        get => _providerManager.IsEnabled(Id);
        set
        {
            if (value == IsEnabled)
            {
                return;
            }

            if (!_providerManager.SetEnabled(Id, value))
            {
                // The manager refused (last usable source); snap the switch back.
                OnPropertyChanged();
                _reportStatus("Settings_Status_SourceMustStayEnabled", string.Empty, true);
                return;
            }

            OnPropertyChanged();
            _reportStatus(value ? "Settings_Status_SourceEnabled" : "Settings_Status_SourceDisabled", Name, false);
        }
    }

    public string SetupUrl => AppInfo.ProvidersDocUrl;

    /// <summary>Re-reads localized text and the availability badge.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(NeedsApiKey));
        OnPropertyChanged(nameof(IsEnabled));
    }
}
