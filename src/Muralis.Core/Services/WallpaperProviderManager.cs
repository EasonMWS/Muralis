using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>A provider that could not answer, with the reason recorded in the log.</summary>
public sealed record ProviderFailure(IWallpaperProvider Provider, string Message);

/// <summary>Wallpapers plus the sources that failed to contribute.</summary>
public sealed record WallpaperSearchResult(IReadOnlyList<Wallpaper> Items, IReadOnlyList<ProviderFailure> Failures);

/// <summary>
/// The single entry point the UI uses to talk to wallpaper sources. Applies the user's
/// enabled/default choices, aggregates searches across sources (a failing source never
/// hides the others), and overlays catalog state (favorites, already-downloaded files)
/// onto the fresh provider results.
/// </summary>
public sealed class WallpaperProviderManager
{
    private readonly IReadOnlyList<IWallpaperProvider> _providers;
    private readonly ISettingsService _settingsService;
    private readonly IProviderConfiguration _configuration;
    private readonly ILocalLibrary _library;
    private readonly ILogger<WallpaperProviderManager> _logger;
    private readonly List<WeakReference<IProviderManagerObserver>> _observers = [];
    private readonly Lock _observerLock = new();

    public WallpaperProviderManager(
        IEnumerable<IWallpaperProvider> providers,
        ISettingsService settingsService,
        IProviderConfiguration configuration,
        ILocalLibrary library,
        ILogger<WallpaperProviderManager> logger)
    {
        _providers = providers.ToList();
        _settingsService = settingsService;
        _configuration = configuration;
        _library = library;
        _logger = logger;
    }

    /// <summary>Subscribes to enabled/default changes without being kept alive by the manager.</summary>
    public void Register(IProviderManagerObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_observerLock)
        {
            _observers.RemoveAll(reference => !reference.TryGetTarget(out _));
            _observers.Add(new WeakReference<IProviderManagerObserver>(observer));
        }
    }

    /// <summary>All registered providers in a stable order.</summary>
    public IReadOnlyList<IWallpaperProvider> Providers => _providers;

    /// <summary>Providers that are enabled and ready to answer (API keys included).</summary>
    public IReadOnlyList<IWallpaperProvider> EnabledProviders =>
        _providers.Where(provider => GetAvailability(provider) == ProviderAvailability.Available).ToList();

    /// <summary>The provider Home uses, falling back to the first usable one.</summary>
    public IWallpaperProvider? DefaultProvider
    {
        get
        {
            var settings = _settingsService.Current.Providers;
            var configured = _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, settings.DefaultProviderId, StringComparison.OrdinalIgnoreCase)
                && GetAvailability(provider) == ProviderAvailability.Available);

            return configured ?? EnabledProviders.FirstOrDefault();
        }
    }

    public ProviderAvailability GetAvailability(IWallpaperProvider provider)
    {
        if (!IsEnabled(provider.Id))
        {
            return ProviderAvailability.Disabled;
        }

        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(_configuration.GetApiKey(provider.Id)))
        {
            return ProviderAvailability.NeedsApiKey;
        }

        return ProviderAvailability.Available;
    }

    public bool IsEnabled(string providerId) =>
        !_settingsService.Current.Providers.DisabledProviders
            .Contains(providerId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Switches a provider on or off. Refuses to switch off the last usable provider so the
    /// app always has at least one source. Returns <c>true</c> when the setting changed.
    /// </summary>
    public bool SetEnabled(string providerId, bool enabled)
    {
        if (!enabled)
        {
            var remaining = _providers.Count(provider =>
                !string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)
                && GetAvailability(provider) != ProviderAvailability.Disabled);

            if (remaining == 0)
            {
                _logger.LogWarning("Refused to disable '{Provider}': it is the last enabled source", providerId);
                return false;
            }
        }

        _settingsService.Update(settings =>
        {
            settings.Providers.DisabledProviders.RemoveAll(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                settings.Providers.DisabledProviders.Add(providerId);
            }
        });

        NotifyChanged();
        return true;
    }

    public void SetDefaultProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _settingsService.Update(settings => settings.Providers.DefaultProviderId = providerId);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        List<IProviderManagerObserver> observers = [];
        lock (_observerLock)
        {
            for (var index = _observers.Count - 1; index >= 0; index--)
            {
                if (_observers[index].TryGetTarget(out var target))
                {
                    observers.Add(target);
                }
                else
                {
                    _observers.RemoveAt(index);
                }
            }
        }

        foreach (var observer in observers)
        {
            observer.OnProvidersChanged();
        }
    }

    /// <summary>
    /// Searches one source or all enabled sources. <paramref name="providerId"/> is
    /// <c>null</c> for "all sources"; the sources' results are interleaved so no single
    /// provider dominates the grid.
    /// </summary>
    public async Task<WallpaperSearchResult> SearchAsync(
        WallpaperQuery query,
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        var targets = ResolveTargets(providerId, out var unavailable);
        if (targets.Count == 0)
        {
            return new WallpaperSearchResult([], unavailable);
        }

        var results = await Task.WhenAll(targets.Select(provider => FetchAsync(provider, query, cancellationToken)))
            .ConfigureAwait(false);

        var failures = new List<ProviderFailure>(unavailable);
        failures.AddRange(results.Where(result => result.Failure is not null).Select(result => result.Failure!));

        var items = Interleave(results.Select(result => result.Items).ToList());
        ApplyCatalogState(items);

        return new WallpaperSearchResult(items, failures);
    }

    /// <summary>
    /// Wallpapers for the Home page, taken from the default provider. When that source
    /// fails the samples bundled with Windows still fill the page, so Home is never empty
    /// just because a network source is down.
    /// </summary>
    public async Task<WallpaperSearchResult> GetFeaturedAsync(int count, CancellationToken cancellationToken = default)
    {
        var failures = new List<ProviderFailure>();
        var provider = DefaultProvider;
        if (provider is not null)
        {
            var result = await FetchAsync(provider, new WallpaperQuery { PageSize = count }, cancellationToken, featured: true)
                .ConfigureAwait(false);

            if (result.Failure is { } failure)
            {
                failures.Add(failure);
            }
            else if (result.Items.Count > 0)
            {
                var featured = result.Items.Take(count).ToList();
                ApplyCatalogState(featured);
                return new WallpaperSearchResult(featured, failures);
            }
        }

        var fallback = _providers.FirstOrDefault(candidate => string.Equals(candidate.Id, "mock", StringComparison.OrdinalIgnoreCase));
        if (fallback is not null && !ReferenceEquals(fallback, provider))
        {
            _logger.LogInformation("Falling back to the sample provider for the featured feed");
            var result = await FetchAsync(fallback, new WallpaperQuery { PageSize = count }, cancellationToken, featured: true)
                .ConfigureAwait(false);

            if (result.Failure is { } failure)
            {
                failures.Add(failure);
            }
            else
            {
                var featured = result.Items.Take(count).ToList();
                ApplyCatalogState(featured);
                return new WallpaperSearchResult(featured, failures);
            }
        }

        return new WallpaperSearchResult([], failures);
    }

    /// <summary>Resolves the URL the download service should fetch for a wallpaper.</summary>
    public Task<string> GetDownloadUrlAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        var provider = FindByWallpaperId(wallpaper.Id);
        return provider is null
            ? Task.FromResult(wallpaper.RemoteUrl ?? string.Empty)
            : provider.GetDownloadUrlAsync(wallpaper, cancellationToken);
    }

    private IWallpaperProvider? FindByWallpaperId(string wallpaperId)
    {
        var separator = wallpaperId.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        var prefix = wallpaperId[..separator];
        return _providers.FirstOrDefault(provider => string.Equals(provider.Id, prefix, StringComparison.OrdinalIgnoreCase));
    }

    private List<IWallpaperProvider> ResolveTargets(string? providerId, out List<ProviderFailure> unavailable)
    {
        unavailable = [];

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return EnabledProviders.ToList();
        }

        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return [];
        }

        if (GetAvailability(provider) != ProviderAvailability.Available)
        {
            unavailable.Add(new ProviderFailure(provider, "The source is not available."));
            return [];
        }

        return [provider];
    }

    private async Task<(IWallpaperProvider Provider, IReadOnlyList<Wallpaper> Items, ProviderFailure? Failure)> FetchAsync(
        IWallpaperProvider provider,
        WallpaperQuery query,
        CancellationToken cancellationToken,
        bool featured = false)
    {
        try
        {
            var items = featured
                ? await provider.GetFeaturedAsync(query.PageSize, cancellationToken).ConfigureAwait(false)
                : await provider.GetWallpapersAsync(query, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Source '{Provider}' returned {Count} wallpapers", provider.Id, items.Count);
            return (provider, items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source '{Provider}' failed to answer", provider.Id);
            return (provider, [], new ProviderFailure(provider, ex.Message));
        }
    }

    private static List<Wallpaper> Interleave(IReadOnlyList<IReadOnlyList<Wallpaper>> sources)
    {
        var merged = new List<Wallpaper>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var longest = sources.Count == 0 ? 0 : sources.Max(items => items.Count);

        for (var index = 0; index < longest; index++)
        {
            foreach (var items in sources)
            {
                if (index < items.Count && seen.Add(items[index].Id))
                {
                    merged.Add(items[index]);
                }
            }
        }

        return merged;
    }

    /// <summary>Copies favorite/downloaded state from the catalog onto fresh provider results.</summary>
    private void ApplyCatalogState(IEnumerable<Wallpaper> items)
    {
        foreach (var item in items)
        {
            if (_library.Find(item.Id) is not { } known)
            {
                continue;
            }

            item.IsFavorite = known.IsFavorite;
            if (known.HasLocalFile)
            {
                item.LocalPath = known.LocalPath;
                if (item.Width <= 0)
                {
                    item.Width = known.Width;
                    item.Height = known.Height;
                }

                if (item.FileSize <= 0)
                {
                    item.FileSize = known.FileSize;
                }
            }
        }
    }
}
