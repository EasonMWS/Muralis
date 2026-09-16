using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Helpers;

namespace Muralis.Core.Canvas;

/// <summary>
/// Reads and writes the desktop canvas prototype layout. The file is deliberately separate from
/// application settings: deleting it resets the prototype to the seed layout, and hand-edits
/// never risk the rest of the app's configuration. Writes are atomic (temp file + move), so a
/// crash mid-save cannot leave a torn layout behind.
/// </summary>
public sealed class CanvasLayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<CanvasLayoutStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public CanvasLayoutStore(ILogger<CanvasLayoutStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? AppPaths.CanvasPrototypeFile;
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Loads the saved layout, or the seed layout when there is nothing valid to load. A file
    /// that does not parse or does not validate is set aside (renamed to <c>.bad</c>) rather than
    /// deleted, so a hand-edited mistake can be recovered.
    /// </summary>
    public async Task<CanvasLayout> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No canvas layout at {Path}; starting from the seed layout", _filePath);
            return CanvasLayout.CreateSeed();
        }

        CanvasLayout? loaded;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            loaded = await JsonSerializer
                .DeserializeAsync<CanvasLayout>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "The canvas layout at {Path} is corrupt; falling back to the seed layout", _filePath);
            SetAsideCorruptFile();
            return CanvasLayout.CreateSeed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the canvas layout at {Path}; falling back to the seed layout", _filePath);
            return CanvasLayout.CreateSeed();
        }

        if (loaded is null)
        {
            return CanvasLayout.CreateSeed();
        }

        var problems = loaded.Validate();
        if (problems.Count > 0)
        {
            _logger.LogError(
                "The canvas layout at {Path} is not valid ({Problems}); falling back to the seed layout",
                _filePath,
                string.Join(" ", problems));
            SetAsideCorruptFile();
            return CanvasLayout.CreateSeed();
        }

        _logger.LogInformation("Canvas layout loaded from {Path} ({Count} items)", _filePath, loaded.Items.Count);
        return loaded;
    }

    /// <summary>Serialises and writes the layout atomically.</summary>
    public async Task SaveAsync(CanvasLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _filePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, layout, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogDebug("Canvas layout saved to {Path}", _filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save the canvas layout to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void SetAsideCorruptFile()
    {
        try
        {
            File.Move(_filePath, _filePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not set aside the invalid canvas layout file");
        }
    }
}
