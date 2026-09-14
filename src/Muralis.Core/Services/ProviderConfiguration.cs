using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;

namespace Muralis.Core.Services;

/// <summary>
/// Reads provider API keys from <c>%LOCALAPPDATA%\Muralis\providers.json</c> (a per-user
/// file that is never part of the repository) and from environment variables. The file is
/// re-read when it changes on disk, so a key can be added without restarting the app.
/// </summary>
public sealed class ProviderConfiguration : IProviderConfiguration
{
    private const string EnvironmentPrefix = "MURALIS_PROVIDER_";
    private const string EnvironmentSuffix = "_API_KEY";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<ProviderConfiguration> _logger;
    private readonly string _filePath;
    private readonly Lock _gate = new();

    private ProviderConfigurationFile? _file;
    private DateTime _loadedAtUtc = DateTime.MinValue;
    private bool _warnedAboutFile;

    public ProviderConfiguration(ILogger<ProviderConfiguration> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? AppPaths.ProvidersFile;
    }

    public string? GetApiKey(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var fromEnvironment = Environment.GetEnvironmentVariable(
            EnvironmentPrefix + providerId.ToUpperInvariant() + EnvironmentSuffix);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var key = ReadFile()?.Providers.TryGetValue(providerId, out var entry) == true
            ? entry.ApiKey
            : null;

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private ProviderConfigurationFile? ReadFile()
    {
        lock (_gate)
        {
            DateTime lastWrite;
            try
            {
                if (!File.Exists(_filePath))
                {
                    _file = null;
                    return null;
                }

                lastWrite = File.GetLastWriteTimeUtc(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the provider configuration at {Path}", _filePath);
                return _file;
            }

            if (_file is not null && lastWrite == _loadedAtUtc)
            {
                return _file;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                _file = JsonSerializer.Deserialize<ProviderConfigurationFile>(json, SerializerOptions) ?? new ProviderConfigurationFile();
                _loadedAtUtc = lastWrite;
                _warnedAboutFile = false;
                _logger.LogInformation(
                    "Provider configuration loaded from {Path} ({Count} entries)",
                    _filePath,
                    _file.Providers.Count);
            }
            catch (JsonException ex)
            {
                if (!_warnedAboutFile)
                {
                    _warnedAboutFile = true;
                    _logger.LogError(ex, "Provider configuration at {Path} is not valid JSON; ignoring it", _filePath);
                }

                _file = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the provider configuration at {Path}", _filePath);
                _file = null;
            }

            return _file;
        }
    }

    private sealed class ProviderConfigurationFile
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, ProviderEntry> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProviderEntry
    {
        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }
    }
}
