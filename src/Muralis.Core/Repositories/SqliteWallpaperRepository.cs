using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Repositories;

/// <summary>
/// SQLite-backed catalog. The schema is created on first use and versioned through
/// the <c>schema_version</c> table so future migrations can be applied in order.
/// </summary>
public sealed class SqliteWallpaperRepository : IWallpaperRepository
{
    private const int CurrentSchemaVersion = 2;
    private const int HistoryRetentionLimit = 200;

    private readonly string _connectionString;
    private readonly ILogger<SqliteWallpaperRepository> _logger;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteWallpaperRepository(ILogger<SqliteWallpaperRepository> logger, string? databasePath = null)
    {
        _logger = logger;

        var path = databasePath ?? AppPaths.DatabaseFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

            if (version < 1)
            {
                await ExecuteAsync(connection, SchemaV1, cancellationToken).ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ExecuteAsync(connection, SchemaV2, cancellationToken).ConfigureAwait(false);
                version = 2;
            }

            await ExecuteAsync(
                connection,
                $"INSERT INTO schema_version (version) VALUES ({version});",
                cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("Wallpaper database ready (schema v{Version})", version);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<IReadOnlyList<Wallpaper>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, local_path, remote_url, thumbnail_url, width, height,
                   file_size, tags, is_favorite, source, created_at, last_used_at, content_hash
            FROM wallpapers
            ORDER BY created_at DESC;
            """;

        var items = new List<Wallpaper>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadWallpaper(reader));
        }

        return items;
    }

    public async Task UpsertAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO wallpapers
                (id, title, local_path, remote_url, thumbnail_url, width, height,
                 file_size, tags, is_favorite, source, created_at, last_used_at, content_hash)
            VALUES
                ($id, $title, $localPath, $remoteUrl, $thumbnailUrl, $width, $height,
                 $fileSize, $tags, $isFavorite, $source, $createdAt, $lastUsedAt, $contentHash)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                local_path = excluded.local_path,
                remote_url = excluded.remote_url,
                thumbnail_url = excluded.thumbnail_url,
                width = excluded.width,
                height = excluded.height,
                file_size = excluded.file_size,
                tags = excluded.tags,
                is_favorite = excluded.is_favorite,
                source = excluded.source,
                last_used_at = excluded.last_used_at,
                content_hash = excluded.content_hash;
            """;

        command.Parameters.AddWithValue("$id", wallpaper.Id);
        command.Parameters.AddWithValue("$title", wallpaper.Title);
        command.Parameters.AddWithValue("$localPath", (object?)wallpaper.LocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$remoteUrl", (object?)wallpaper.RemoteUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumbnailUrl", (object?)wallpaper.ThumbnailUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", wallpaper.Width);
        command.Parameters.AddWithValue("$height", wallpaper.Height);
        command.Parameters.AddWithValue("$fileSize", wallpaper.FileSize);
        command.Parameters.AddWithValue("$tags", string.Join(',', wallpaper.Tags));
        command.Parameters.AddWithValue("$isFavorite", wallpaper.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$source", (int)wallpaper.Source);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(wallpaper.CreatedAt));
        command.Parameters.AddWithValue("$lastUsedAt", wallpaper.LastUsedAt is { } used ? FormatTimestamp(used) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$contentHash", (object?)wallpaper.ContentHash ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string wallpaperId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(wallpaperId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM wallpapers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", wallpaperId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO history (wallpaper_id, title, applied_at, monitor_name)
                VALUES ($id, $title, $appliedAt, $monitor);
                """;
            command.Parameters.AddWithValue("$id", entry.WallpaperId);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$appliedAt", FormatTimestamp(entry.AppliedAt));
            command.Parameters.AddWithValue("$monitor", (object?)entry.MonitorName ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Keep history bounded so the file never grows without limit.
        await using (var trim = connection.CreateCommand())
        {
            trim.CommandText = """
                DELETE FROM history
                WHERE id NOT IN (
                    SELECT id FROM history ORDER BY applied_at DESC LIMIT $limit
                );
                """;
            trim.Parameters.AddWithValue("$limit", HistoryRetentionLimit);
            await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<Wallpaper>> GetRecentlyUsedAsync(int limit, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, w.title, w.local_path, w.remote_url, w.thumbnail_url, w.width, w.height,
                   w.file_size, w.tags, w.is_favorite, w.source, w.created_at, w.last_used_at, w.content_hash
            FROM wallpapers w
            JOIN (
                SELECT wallpaper_id, MAX(applied_at) AS last_used
                FROM history
                GROUP BY wallpaper_id
            ) h ON h.wallpaper_id = w.id
            ORDER BY h.last_used DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var items = new List<Wallpaper>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadWallpaper(reader));
        }

        return items;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        var exists = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return 0;
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM schema_version;";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long version ? (int)version : 0;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Wallpaper ReadWallpaper(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        LocalPath = reader.IsDBNull(2) ? null : reader.GetString(2),
        RemoteUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
        ThumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
        Width = reader.GetInt32(5),
        Height = reader.GetInt32(6),
        FileSize = reader.GetInt64(7),
        Tags = reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IsFavorite = reader.GetInt32(9) != 0,
        Source = (WallpaperSource)reader.GetInt32(10),
        CreatedAt = ParseTimestamp(reader.GetString(11)),
        LastUsedAt = reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
        ContentHash = reader.IsDBNull(13) ? null : reader.GetString(13),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS wallpapers (
            id            TEXT PRIMARY KEY,
            title         TEXT NOT NULL DEFAULT '',
            local_path    TEXT NULL,
            remote_url    TEXT NULL,
            thumbnail_url TEXT NULL,
            width         INTEGER NOT NULL DEFAULT 0,
            height        INTEGER NOT NULL DEFAULT 0,
            file_size     INTEGER NOT NULL DEFAULT 0,
            tags          TEXT NOT NULL DEFAULT '',
            is_favorite   INTEGER NOT NULL DEFAULT 0,
            source        INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT NOT NULL,
            last_used_at  TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_wallpapers_favorite ON wallpapers (is_favorite);
        CREATE INDEX IF NOT EXISTS ix_wallpapers_source ON wallpapers (source);

        CREATE TABLE IF NOT EXISTS history (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            wallpaper_id TEXT NOT NULL,
            title        TEXT NOT NULL DEFAULT '',
            applied_at   TEXT NOT NULL,
            monitor_name TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_history_applied_at ON history (applied_at DESC);
        """;

    private const string SchemaV2 = """
        ALTER TABLE wallpapers ADD COLUMN content_hash TEXT NULL;
        CREATE INDEX IF NOT EXISTS ix_wallpapers_content_hash ON wallpapers (content_hash);
        """;
}
