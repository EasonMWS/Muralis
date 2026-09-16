using Muralis.App.Services;
using Muralis.Core.Desktop.Takeover;

namespace Muralis.App.ViewModels;

/// <summary>
/// One entry of the desktop-mode picker: the mode itself and the two lines that name and explain it.
/// The text is resolved once, when the picker is filled, so the list stays a plain binding source.
/// </summary>
public sealed class DesktopModeRow
{
    public DesktopModeRow(DesktopMode mode, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);

        Mode = mode;
        Label = localization.Get($"Dynamic_Desktop_Mode_{mode}");
        Description = localization.Get($"Dynamic_Desktop_Mode_{mode}_Description");
    }

    public DesktopMode Mode { get; }

    public string Label { get; }

    public string Description { get; }
}
