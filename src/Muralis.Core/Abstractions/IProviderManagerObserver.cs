namespace Muralis.Core.Abstractions;

/// <summary>
/// Observes wallpaper-source settings (enabled sources, default source). References are
/// held weakly so transient view models are not kept alive.
/// </summary>
public interface IProviderManagerObserver
{
    void OnProvidersChanged();
}
