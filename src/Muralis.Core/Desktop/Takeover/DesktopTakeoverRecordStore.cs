using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Helpers;

namespace Muralis.Core.Desktop.Takeover;

/// <summary>What came of looking for the takeover marker.</summary>
/// <param name="Record">The marker, when there is a readable one.</param>
/// <param name="Error">Why there is no record to use, when the file was there but unusable.</param>
public sealed record DesktopTakeoverMarker(DesktopTakeoverRecord? Record, string? Error)
{
    /// <summary>No file at all: the ordinary case, and the one that means nothing was taken over.</summary>
    public static DesktopTakeoverMarker None { get; } = new(null, null);

    /// <summary>A file was there but could not be used, so what it said must not be acted on.</summary>
    public bool IsUnreadable => Record is null && Error is not null;

    /// <summary>The desktop may still be taken over from a previous run.</summary>
    public bool NeedsRecovery => Record is { TakeoverWasActive: true };
}

/// <summary>
/// Reads, writes and removes the desktop takeover marker. The file is tiny and written atomically, so
/// a crash during the write cannot leave half a record behind.
/// </summary>
/// <remarks>
/// Reading is synchronous on purpose: the marker is checked while the app is starting, before any of
/// the work that could itself be slow — the user's desktop has to be given back first, and waiting on
/// a thread pool hop to find that out would only delay it.
/// </remarks>
public sealed class DesktopTakeoverRecordStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<DesktopTakeoverRecordStore> _logger;
    private readonly string _filePath;

    public DesktopTakeoverRecordStore(ILogger<DesktopTakeoverRecordStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? AppPaths.DesktopTakeoverFile;
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Looks for the marker. A file that cannot be read is set aside rather than deleted — it is the
    /// only record of what the desktop looked like — and reported as unreadable, so the caller knows
    /// it must not touch the desktop on the strength of it.
    /// </summary>
    public DesktopTakeoverMarker Load()
    {
        if (!File.Exists(_filePath))
        {
            return DesktopTakeoverMarker.None;
        }

        DesktopTakeoverRecord? record;
        try
        {
            var json = File.ReadAllText(_filePath);
            record = JsonSerializer.Deserialize<DesktopTakeoverRecord>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogError(ex, "The desktop takeover marker at {Path} could not be read", _filePath);
            SetAside();
            return new DesktopTakeoverMarker(null, $"the marker could not be read ({ex.Message})");
        }

        if (record is null)
        {
            SetAside();
            return new DesktopTakeoverMarker(null, "the marker was empty");
        }

        var problems = record.Validate();
        if (problems.Count > 0)
        {
            _logger.LogError(
                "The desktop takeover marker at {Path} is not usable ({Problems})",
                _filePath,
                string.Join(" ", problems));
            SetAside();
            return new DesktopTakeoverMarker(null, string.Join(" ", problems));
        }

        _logger.LogInformation(
            "A desktop takeover marker from {When} was found (run {Session}, process {ProcessId}, state {State})",
            record.Timestamp,
            record.SessionId,
            record.ProcessId,
            record.State);
        return new DesktopTakeoverMarker(record, null);
    }

    /// <summary>
    /// Writes the marker atomically and says whether it is now on disk. The answer matters: the marker
    /// is what a crash would be detected by, so a takeover that cannot leave one must not hide anything.
    /// </summary>
    public bool TrySave(DesktopTakeoverRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var started = Stopwatch.GetTimestamp();
            var tempPath = _filePath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, record, SerializerOptions);
            }

            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogDebug(
                "The desktop takeover marker was written in {Elapsed:0.0} ms",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The desktop takeover marker at {Path} could not be written", _filePath);
            return false;
        }
    }

    /// <summary>
    /// Removes the marker and says whether it is gone. A marker that cannot be removed only means the
    /// next launch checks a desktop that is already fine, so this is reported, not raised.
    /// </summary>
    public bool TryClear()
    {
        try
        {
            var removed = false;
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                removed = true;
            }

            var tempPath = _filePath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (removed)
            {
                _logger.LogInformation("The desktop takeover marker was removed");
            }

            return !File.Exists(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The desktop takeover marker at {Path} could not be removed", _filePath);
            return false;
        }
    }

    /// <summary>Keeps an unreadable marker beside the file instead of deleting what it says.</summary>
    private void SetAside()
    {
        try
        {
            File.Move(_filePath, _filePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The unreadable desktop takeover marker could not be set aside");
        }
    }
}
