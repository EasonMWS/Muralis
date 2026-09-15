using Muralis.Core.Models;

namespace Muralis.Desktop.Monitors;

/// <summary>The display set changed; carries the snapshot that replaced the previous one.</summary>
public sealed record MonitorsChanged(IReadOnlyList<Monitor> Monitors);
