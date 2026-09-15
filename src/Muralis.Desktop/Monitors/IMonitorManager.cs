using Muralis.Core.Models;

namespace Muralis.Desktop.Monitors;

/// <summary>
/// Owns the current display snapshot: which displays exist, which one is primary, and which
/// <see cref="Monitor"/> a reference points at right now. Consumers never enumerate displays
/// themselves — this is the single source of monitor truth.
/// </summary>
public interface IMonitorManager
{
    /// <summary>All displays, in runtime order. Mirrored duplicates are already collapsed.</summary>
    IReadOnlyList<Monitor> Monitors { get; }

    /// <summary>The primary display, or <c>null</c> when no display is marked primary.</summary>
    Monitor? Primary { get; }

    /// <summary>Resolves a reference to the display it points at right now, or <c>null</c> when it is gone.</summary>
    Monitor? Resolve(MonitorRef monitor);

    /// <summary>Raised when the display set changed (plug, unplug, rearrangement, DPI or resolution change).</summary>
    event EventHandler<MonitorsChanged>? Changed;
}
