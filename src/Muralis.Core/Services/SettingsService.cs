using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Lock _mutateLock = new();
    private AppSettings _current = new();

    public SettingsService(ILogger<SettingsService> logger, string? settingsFilePath = null)
    {
        _logger = logger;
        _settingsFilePath = settingsFilePath ?? AppPaths.SettingsFile;
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                _logger.LogInformation("No settings file at {Path}; starting with defaults", _settingsFilePath);
                _current = new AppSettings();
                return;
            }

            await using var stream = File.OpenRead(_settingsFilePath);
            var loaded = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            _current = loaded ?? new AppSettings();
            _logger.LogInformation("Settings loaded from {Path}", _settingsFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Settings file {Path} is corrupt; falling back to defaults", _settingsFilePath);
            BackupCorruptFile();
            _current = new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read settings file {Path}; falling back to defaults", _settingsFilePath);
            _current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temp file first so a crash mid-write cannot corrupt settings.
            var tempPath = _settingsFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, _current, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, _settingsFilePath, overwrite: true);
            _logger.LogDebug("Settings saved to {Path}", _settingsFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsFilePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_mutateLock)
        {
            mutate(_current);
        }

        SettingsChanged?.Invoke(this, _current);
        _ = SaveDetachedAsync();
    }

    private async Task SaveDetachedAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SaveAsync already handles I/O failures; this is a last-resort guard.
            _logger.LogError(ex, "Background settings save failed unexpectedly");
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            File.Move(_settingsFilePath, _settingsFilePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not back up corrupt settings file");
        }
    }
}
