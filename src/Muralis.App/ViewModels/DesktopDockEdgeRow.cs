using Muralis.App.Services;
using Muralis.Core.Dock;

namespace Muralis.App.ViewModels;

/// <summary>
/// One entry of the dock's edge picker: the edge itself and the name to show for it. The name is
/// resolved once, when the picker is filled, so the list stays a plain binding source.
/// </summary>
public sealed class DesktopDockEdgeRow
{
    public DesktopDockEdgeRow(DockEdge edge, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);

        Edge = edge;
        Label = localization.Get($"Dynamic_Dock_Edge_{edge}");
    }

    public DockEdge Edge { get; }

    public string Label { get; }
}
