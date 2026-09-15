using Muralis.Core.Models;

namespace Muralis.Desktop.Monitors;

/// <summary>
/// Holds the current display snapshot behind <see cref="IMonitorManager"/>. Deliberately a pure
/// in-memory model for now: whoever produces <see cref="Monitor"/> instances (the Win32
/// enumeration in a later commit) calls <see cref="Update"/>; matching and change notification
/// live here so consumers never re-derive them.
/// </summary>
public sealed class MonitorManager : IMonitorManager
{
    private Monitor[] _monitors = [];

    public IReadOnlyList<Monitor> Monitors => _monitors;

    public Monitor? Primary => Array.Find(_monitors, monitor => monitor.Runtime.IsPrimary);

    public event EventHandler<MonitorsChanged>? Changed;

    /// <summary>Replaces the snapshot and raises <see cref="Changed"/> only when it actually differs.</summary>
    public void Update(IReadOnlyList<Monitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (_monitors.SequenceEqual(monitors))
        {
            return;
        }

        _monitors = [.. monitors];
        Changed?.Invoke(this, new MonitorsChanged(_monitors));
    }

    public Monitor? Resolve(MonitorRef monitor) =>
        Array.Find(_monitors, candidate => string.Equals(candidate.Identity.StableId, monitor.StableId, StringComparison.Ordinal));
}
