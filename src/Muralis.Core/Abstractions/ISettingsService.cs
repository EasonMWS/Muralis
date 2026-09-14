using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// Loads, mutates and persists <see cref="AppSettings"/>. Mutations made through
/// <see cref="Update"/> are saved in the background; call <see cref="SaveAsync"/>
/// to flush synchronously (e.g. during shutdown).
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    void Update(Action<AppSettings> mutate);
}
