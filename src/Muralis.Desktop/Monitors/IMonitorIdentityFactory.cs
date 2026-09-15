using Muralis.Core.Models;

namespace Muralis.Desktop.Monitors;

/// <summary>
/// Derives the persistent identity of a display from its runtime facts and, when readable, its
/// EDID block. Left as a seam on purpose: the real <see cref="MonitorIdentity.StableId"/>
/// derivation (EDID hash, or the documented signature fallback) is decided together with the
/// Win32 display enumeration, and must not be faked before then.
/// </summary>
public interface IMonitorIdentityFactory
{
    MonitorIdentity Create(MonitorRuntimeInfo runtime, EdidInfo? edid);
}
