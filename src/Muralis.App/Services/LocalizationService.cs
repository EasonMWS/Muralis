using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Muralis.App.Services;

/// <summary>An observer that wants to refresh its displayed text when the language changes.</summary>
public interface ILanguageAware
{
    void OnLanguageChanged();
}

/// <summary>User-selectable display language.</summary>
/// <param name="Preference">Persisted value; empty means "follow the system language".</param>
/// <param name="DisplayName">Label shown in the settings, written in the language itself.</param>
public sealed record LanguageOption(string Preference, string DisplayName);

/// <summary>
/// Resolves UI strings from the embedded resources (<c>Strings/Resources.resx</c> plus one
/// satellite per language) and applies the user's display-language preference at runtime.
/// </summary>
public interface ILocalizationService
{
    /// <summary>XAML-friendly accessor: <c>{Binding [Key], Source={StaticResource Loc}}</c>.</summary>
    string this[string key] { get; }

    string Get(string key);

    /// <summary>Looks up <paramref name="key"/> and falls back to <paramref name="fallback"/> when no translation exists.</summary>
    string GetOrFallback(string key, string fallback);

    string Format(string key, params object?[] args);

    /// <summary>The persisted preference; empty means "follow the system language".</summary>
    string Preference { get; }

    /// <summary>The BCP-47 culture actually in use, after resolving the system language.</summary>
    string EffectiveLanguage { get; }

    IReadOnlyList<LanguageOption> Languages { get; }

    event EventHandler? LanguageChanged;

    /// <summary>Applies a persisted preference (empty = system). Must be called on the UI thread.</summary>
    void Apply(string preference);

    /// <summary>
    /// Subscribes an observer for language changes. References are held weakly so that
    /// transient view models are not kept alive by this singleton.
    /// </summary>
    void Register(ILanguageAware observer);
}

public sealed class LocalizationService : ILocalizationService, INotifyPropertyChanged
{
    /// <summary>
    /// Name under which XAML bindings reach the indexer. Both names are raised because
    /// the binding engine's convention differs between WPF and WinUI.
    /// </summary>
    private const string IndexerPropertyName = "Item[]";

    private static readonly ResourceManager Resources =
        new("Muralis.App.Strings.Resources", typeof(LocalizationService).Assembly);

    private readonly ILogger<LocalizationService> _logger;
    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly List<WeakReference<ILanguageAware>> _observers = [];
    private readonly HashSet<string> _missingKeys = [];
    private readonly Lock _observerLock = new();

    private CultureInfo _culture = CultureInfo.InstalledUICulture;
    private string _preference = string.Empty;
    private bool _applied;

    public LocalizationService(ILogger<LocalizationService> logger) => _logger = logger;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    public string Preference => _preference;

    public string EffectiveLanguage => _culture.Name;

    public IReadOnlyList<LanguageOption> Languages =>
    [
        new LanguageOption(string.Empty, Get("Settings_Language_FollowSystem")),
        new LanguageOption("en-US", "English"),
        new LanguageOption("zh-CN", "简体中文"),
    ];

    public string Get(string key)
    {
        var value = Resources.GetString(key, _culture);
        if (value is not null)
        {
            return value;
        }

        LogMissingKey(key);
        return key;
    }

    public string GetOrFallback(string key, string fallback)
    {
        var value = Resources.GetString(key, _culture);
        if (value is not null)
        {
            return value;
        }

        LogMissingKey(key);
        return fallback;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    public void Apply(string preference)
    {
        var normalized = preference?.Trim() ?? string.Empty;
        var culture = ResolveCulture(normalized);
        if (_applied && normalized == _preference && culture.Name == _culture.Name)
        {
            return;
        }

        _preference = normalized;
        _culture = culture;
        _applied = true;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        _logger.LogInformation("Display language applied: preference '{Preference}' → {Culture}", normalized, culture.Name);
        NotifyLanguageChanged();
    }

    public void Register(ILanguageAware observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_observerLock)
        {
            _observers.RemoveAll(reference => !reference.TryGetTarget(out _));
            _observers.Add(new WeakReference<ILanguageAware>(observer));
        }
    }

    private void NotifyLanguageChanged()
    {
        if (_dispatcherQueue is { } dispatcher && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(NotifyLanguageChanged);
            return;
        }

        // Refresh XAML text bound through the indexer.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerPropertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));

        LanguageChanged?.Invoke(this, EventArgs.Empty);

        List<ILanguageAware> observers = [];
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
            observer.OnLanguageChanged();
        }
    }

    private static CultureInfo ResolveCulture(string preference)
    {
        if (!string.IsNullOrEmpty(preference))
        {
            try
            {
                return CultureInfo.GetCultureInfo(preference);
            }
            catch (CultureNotFoundException)
            {
                // Unknown code on disk (hand-edited settings): fall through to the system language.
            }
        }

        var system = CultureInfo.InstalledUICulture;

        // zh-TW/zh-HK and similar map to the Simplified resources we ship rather than
        // dropping to English.
        return string.Equals(system.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : system;
    }

    private void LogMissingKey(string key)
    {
        lock (_observerLock)
        {
            if (!_missingKeys.Add(key))
            {
                return;
            }
        }

        _logger.LogWarning("Missing localization resource for key '{Key}'", key);
    }
}
