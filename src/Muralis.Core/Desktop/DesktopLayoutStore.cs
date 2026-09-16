using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Helpers;

namespace Muralis.Core.Desktop;

/// <summary>
/// Reads and writes the desktop layout document. The file is deliberately separate from
/// application settings: deleting it resets the desktop to an empty one, and hand-edits never risk
/// the rest of the app's configuration. Writes are atomic (temp file + move), so a crash mid-save
/// cannot leave a torn layout behind.
/// </summary>
/// <remarks>
/// Documents written by the Phase 2 prototype (schema 1) live in their own file next to this one.
/// They are read once and turned into a current document — the parameters carry over, the prototype
/// tiles cannot, because none of them pointed at anything — and the old file is kept beside the new
/// one as <c>.v1.bak</c>. A prototype file that cannot be understood is left exactly where it is.
/// </remarks>
public sealed class DesktopLayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<DesktopLayoutStore> _logger;
    private readonly string _filePath;
    private readonly string? _prototypeFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public DesktopLayoutStore(ILogger<DesktopLayoutStore> logger, string? filePath = null, string? prototypeFilePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? AppPaths.DesktopLayoutFile;
        _prototypeFilePath = prototypeFilePath ?? (filePath is null ? AppPaths.CanvasPrototypeFile : null);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Loads the saved layout, or an empty one when there is nothing valid to load. A file that does
    /// not parse or does not validate is set aside (renamed to <c>.bad</c>) rather than deleted, so a
    /// hand-edited mistake can be recovered. A file written by an older version is brought forward
    /// and written back in the current shape, with the original kept beside it.
    /// </summary>
    public async Task<DesktopLayout> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return await MigrateOrStartEmptyAsync(cancellationToken).ConfigureAwait(false);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the desktop layout at {Path}; starting from an empty layout", _filePath);
            return DesktopLayout.CreateEmpty();
        }

        var reported = DesktopLayoutMigrator.SchemaVersionOf(json);
        if (reported is null)
        {
            _logger.LogError("The desktop layout at {Path} is not readable as a document; starting from an empty layout", _filePath);
            SetAsideCorruptFile();
            return DesktopLayout.CreateEmpty();
        }

        var version = reported.Value;

        if (version == 1)
        {
            // A prototype document that was renamed or moved here: the same read as the prototype
            // file itself, and nothing about it can be written back as it was.
            var prototype = DesktopLayoutMigrator.MigrateFromPrototype(json);
            if (prototype.Layout is null)
            {
                _logger.LogError(
                    "The desktop layout at {Path} could not be brought forward ({Error}); starting from an empty layout",
                    _filePath,
                    prototype.Error);
                SetAsideCorruptFile();
                return DesktopLayout.CreateEmpty();
            }

            await SaveAsync(prototype.Layout, cancellationToken).ConfigureAwait(false);
            return prototype.Layout;
        }

        // A version that is not there at all is read as the current shape: the document is meant to
        // be edited by hand, and a hand edit that drops the number should still be judged by what the
        // file says rather than guessed at. Only a version that is really older is brought forward.
        if (version > 1 && version < DesktopLayout.CurrentSchemaVersion)
        {
            var upgraded = DesktopLayoutMigrator.UpgradeFromVersion2(json);
            if (upgraded.Layout is null)
            {
                _logger.LogError(
                    "The desktop layout at {Path} could not be brought forward from version {Version} ({Error}); starting from an empty layout",
                    _filePath,
                    version,
                    upgraded.Error);
                SetAsideCorruptFile();
                return DesktopLayout.CreateEmpty();
            }

            KeepAsCopy(_filePath + $".v{version}.bak", version);
            await SaveAsync(upgraded.Layout, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "The version {Version} desktop layout was brought forward: {Docked} of its {Count} items are in the dock",
                version,
                upgraded.Layout.Dock.Entries.Count,
                upgraded.Layout.Items.Count);
            return upgraded.Layout;
        }

        DesktopLayout? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<DesktopLayout>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // The document is meant to be edited by hand, so a target whose kind is missing or
            // unknown arrives as a shape the serialiser will not map rather than as broken JSON.
            _logger.LogError(ex, "The desktop layout at {Path} is corrupt; starting from an empty layout", _filePath);
            SetAsideCorruptFile();
            return DesktopLayout.CreateEmpty();
        }

        if (loaded is null)
        {
            return DesktopLayout.CreateEmpty();
        }

        var problems = loaded.Validate();
        if (problems.Count > 0)
        {
            _logger.LogError(
                "The desktop layout at {Path} is not valid ({Problems}); starting from an empty layout",
                _filePath,
                string.Join(" ", problems));
            SetAsideCorruptFile();
            return DesktopLayout.CreateEmpty();
        }

        _logger.LogInformation("Desktop layout loaded from {Path} ({Count} items)", _filePath, loaded.Items.Count);
        return loaded;
    }

    /// <summary>Serialises and writes the layout atomically.</summary>
    public async Task SaveAsync(DesktopLayout layout, CancellationToken cancellationToken = default)
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

            var started = Stopwatch.GetTimestamp();
            var tempPath = _filePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, layout, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogInformation(
                "Desktop layout saved to {Path} ({Count} items) in {Elapsed:0.0} ms",
                _filePath,
                layout.Items.Count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save the desktop layout to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Brings a Phase 2 prototype layout forward when there is one: the parameters carry over, the
    /// prototype tiles do not, and the old file is kept next to the new one. Without a prototype file
    /// this is simply the first run of an empty desktop.
    /// </summary>
    private async Task<DesktopLayout> MigrateOrStartEmptyAsync(CancellationToken cancellationToken)
    {
        if (_prototypeFilePath is null || !File.Exists(_prototypeFilePath))
        {
            _logger.LogInformation("No desktop layout at {Path}; starting with an empty desktop", _filePath);
            return DesktopLayout.CreateEmpty();
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_prototypeFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the prototype layout at {Path}; starting with an empty desktop", _prototypeFilePath);
            return DesktopLayout.CreateEmpty();
        }

        var migration = DesktopLayoutMigrator.MigrateFromPrototype(json);
        if (migration.Layout is null)
        {
            // The file is left exactly where it is: a failed migration must not cost the user the
            // only copy of what they had.
            _logger.LogError(
                "The prototype layout at {Path} could not be migrated ({Error}); it was left in place and the desktop starts empty",
                _prototypeFilePath,
                migration.Error);
            return DesktopLayout.CreateEmpty();
        }

        var layout = migration.Layout;
        try
        {
            File.Move(_prototypeFilePath, _prototypeFilePath + ".v1.bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The migrated prototype layout at {Path} could not be kept as .v1.bak", _prototypeFilePath);
        }

        await SaveAsync(layout, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "The prototype layout was migrated: parameters carried over, {Dropped} prototype items were not",
            migration.DroppedItems);
        return layout;
    }

    private void SetAsideCorruptFile()
    {
        try
        {
            File.Move(_filePath, _filePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not set aside the invalid desktop layout file");
        }
    }

    /// <summary>
    /// Keeps a copy of the document that is about to be written over, so a version that was brought
    /// forward can always be read back in the shape the user's own version wrote.
    /// </summary>
    private void KeepAsCopy(string backupPath, int version)
    {
        try
        {
            File.Copy(_filePath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The version {Version} desktop layout could not be kept as {Backup}", version, backupPath);
        }
    }
}
