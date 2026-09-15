namespace Muralis.Core.Models;

/// <summary>
/// A lightweight reference to one display, identified by its stable id. Unlike
/// <see cref="Monitor"/> it carries no runtime facts, so it stays valid across topology changes
/// — consumers resolve it through <c>IMonitorManager</c> when they need the current facts.
/// </summary>
public readonly record struct MonitorRef(string StableId)
{
    public static MonitorRef From(Monitor monitor) => new(monitor.Identity.StableId);
}
