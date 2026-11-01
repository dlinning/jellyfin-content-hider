using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Standalone on-disk SQLite repository for persisting per-user content blocks and toggle states.
/// Completely isolated from Jellyfin's primary databases.
/// </summary>
public sealed class SqliteContentBlockRepository : IContentBlockRepository
{
    private const string PragmaAndSchemaSql = """
        PRAGMA journal_mode = DELETE;
        PRAGMA synchronous = FULL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS content_blocks (
            user_id      TEXT NOT NULL,
            media_id     TEXT NOT NULL,
            media_type   TEXT NOT NULL DEFAULT '',
            media_name   TEXT NOT NULL DEFAULT '',
            created_at   TEXT NOT NULL,
            PRIMARY KEY (user_id, media_id)
        );

        CREATE INDEX IF NOT EXISTS idx_content_blocks_user_id
            ON content_blocks(user_id);

        CREATE TABLE IF NOT EXISTS user_block_settings (
            user_id               TEXT PRIMARY KEY,
            is_blocking_enabled   INTEGER NOT NULL DEFAULT 1,
            disabled_until_utc    TEXT NULL,
            magic_playlist_id     TEXT NULL,
            updated_at            TEXT NOT NULL
        );
        """;

    private const string UpsertBlockSql = """
        INSERT INTO content_blocks (user_id, media_id, media_type, media_name, created_at)
        VALUES ($userId, $mediaId, $mediaType, $mediaName, $createdAt)
        ON CONFLICT(user_id, media_id) DO UPDATE SET
            media_type = excluded.media_type,
            media_name = CASE WHEN excluded.media_name <> '' THEN excluded.media_name ELSE content_blocks.media_name END,
            created_at = excluded.created_at;
        """;

    private const string RemoveBlockSql = """
        DELETE FROM content_blocks
        WHERE user_id = $userId AND media_id = $mediaId;
        """;

    private const string GetUserBlocksSql = """
        SELECT user_id, media_id, media_type, media_name, created_at
        FROM content_blocks
        WHERE user_id = $userId
        ORDER BY created_at DESC;
        """;

    private const string GetAllBlocksSql = """
        SELECT user_id, media_id, media_type, media_name, created_at
        FROM content_blocks
        ORDER BY created_at DESC;
        """;

    private const string GetUserSettingsSql = """
        SELECT user_id, is_blocking_enabled, disabled_until_utc, magic_playlist_id, updated_at
        FROM user_block_settings
        WHERE user_id = $userId;
        """;

    private const string GetAllUserSettingsSql = """
        SELECT user_id, is_blocking_enabled, disabled_until_utc, magic_playlist_id, updated_at
        FROM user_block_settings;
        """;

    private const string UpsertUserSettingsSql = """
        INSERT INTO user_block_settings (user_id, is_blocking_enabled, disabled_until_utc, magic_playlist_id, updated_at)
        VALUES ($userId, $isEnabled, $disabledUntil, $magicPlaylistId, $updatedAt)
        ON CONFLICT(user_id) DO UPDATE SET
            is_blocking_enabled = excluded.is_blocking_enabled,
            disabled_until_utc = excluded.disabled_until_utc,
            magic_playlist_id = COALESCE(excluded.magic_playlist_id, user_block_settings.magic_playlist_id),
            updated_at = excluded.updated_at;
        """;

    private const string DeleteUserBlocksSql = "DELETE FROM content_blocks WHERE user_id = $userId;";
    private const string DeleteUserSettingsSql = "DELETE FROM user_block_settings WHERE user_id = $userId;";
    private const string DeleteMediaBlocksSql = "DELETE FROM content_blocks WHERE media_id = $mediaId;";

    private readonly Lock _syncLock = new();
    private readonly Func<string> _pathResolver;
    private string? _initializedPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteContentBlockRepository"/> class for DI.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    public SqliteContentBlockRepository(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _pathResolver = () =>
        {
            if (Plugin.Instance is { } plugin)
            {
                return plugin.DatabaseFilePath;
            }

            return Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.ContentHider", "content_blocks.sqlite");
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteContentBlockRepository"/> class with an explicit on-disk file path.
    /// </summary>
    /// <param name="databaseFilePath">The explicit file path for the on-disk SQLite database.</param>
    public SqliteContentBlockRepository(string databaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFilePath);
        _pathResolver = () => databaseFilePath;
    }

    /// <inheritdoc />
    public string DatabaseFilePath => Path.GetFullPath(_pathResolver());

    /// <inheritdoc />
    public void EnsureInitialized()
    {
        lock (_syncLock)
        {
            EnsureInitializedCore();
        }
    }

    /// <inheritdoc />
    public void UpsertBlock(ContentBlockRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertBlockSql;
            command.Parameters.AddWithValue("$userId", FormatGuid(record.UserId));
            command.Parameters.AddWithValue("$mediaId", FormatGuid(record.MediaId));
            command.Parameters.AddWithValue("$mediaType", record.MediaType ?? string.Empty);
            command.Parameters.AddWithValue("$mediaName", record.MediaName ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", FormatUtc(record.CreatedAtUtc));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public bool RemoveBlock(Guid userId, Guid mediaId)
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = RemoveBlockSql;
            command.Parameters.AddWithValue("$userId", FormatGuid(userId));
            command.Parameters.AddWithValue("$mediaId", FormatGuid(mediaId));
            var affected = command.ExecuteNonQuery();
            transaction.Commit();
            return affected > 0;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId)
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var command = connection.CreateCommand();
            command.CommandText = GetUserBlocksSql;
            command.Parameters.AddWithValue("$userId", FormatGuid(userId));
            using var reader = command.ExecuteReader();
            var list = new List<ContentBlockRecord>();
            while (reader.Read())
            {
                if (TryReadBlockRecord(reader, out var record))
                {
                    list.Add(record);
                }
            }

            return list;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetAllBlocks()
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var command = connection.CreateCommand();
            command.CommandText = GetAllBlocksSql;
            using var reader = command.ExecuteReader();
            var list = new List<ContentBlockRecord>();
            while (reader.Read())
            {
                if (TryReadBlockRecord(reader, out var record))
                {
                    list.Add(record);
                }
            }

            return list;
        }
    }

    /// <inheritdoc />
    public UserBlockSettings? GetUserSettings(Guid userId)
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var command = connection.CreateCommand();
            command.CommandText = GetUserSettingsSql;
            command.Parameters.AddWithValue("$userId", FormatGuid(userId));
            using var reader = command.ExecuteReader();
            if (reader.Read() && TryReadUserSettings(reader, out var settings))
            {
                return settings;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UserBlockSettings> GetAllUserSettings()
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var command = connection.CreateCommand();
            command.CommandText = GetAllUserSettingsSql;
            using var reader = command.ExecuteReader();
            var list = new List<UserBlockSettings>();
            while (reader.Read())
            {
                if (TryReadUserSettings(reader, out var settings))
                {
                    list.Add(settings);
                }
            }

            return list;
        }
    }

    /// <inheritdoc />
    public void UpsertUserSettings(UserBlockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertUserSettingsSql;
            command.Parameters.AddWithValue("$userId", FormatGuid(settings.UserId));
            command.Parameters.AddWithValue("$isEnabled", settings.IsBlockingEnabled ? 1 : 0);
            command.Parameters.AddWithValue(
                "$disabledUntil",
                settings.DisabledUntilUtc.HasValue ? FormatUtc(settings.DisabledUntilUtc.Value) : DBNull.Value);
            command.Parameters.AddWithValue(
                "$magicPlaylistId",
                settings.MagicPlaylistId.HasValue ? FormatGuid(settings.MagicPlaylistId.Value) : DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", FormatUtc(settings.UpdatedAtUtc));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public int DeleteAllUserData(Guid userId)
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var transaction = connection.BeginTransaction();
            var formattedUserId = FormatGuid(userId);

            using var cmdBlocks = connection.CreateCommand();
            cmdBlocks.Transaction = transaction;
            cmdBlocks.CommandText = DeleteUserBlocksSql;
            cmdBlocks.Parameters.AddWithValue("$userId", formattedUserId);
            var deleted = cmdBlocks.ExecuteNonQuery();

            using var cmdSettings = connection.CreateCommand();
            cmdSettings.Transaction = transaction;
            cmdSettings.CommandText = DeleteUserSettingsSql;
            cmdSettings.Parameters.AddWithValue("$userId", formattedUserId);
            deleted += cmdSettings.ExecuteNonQuery();

            transaction.Commit();
            return deleted;
        }
    }

    /// <inheritdoc />
    public int DeleteMediaBlocks(Guid mediaId)
    {
        lock (_syncLock)
        {
            using var connection = OpenConnectionCore();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = DeleteMediaBlocksSql;
            command.Parameters.AddWithValue("$mediaId", FormatGuid(mediaId));
            var deleted = command.ExecuteNonQuery();
            transaction.Commit();
            return deleted;
        }
    }

    private static string FormatGuid(Guid id) => id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryReadBlockRecord(SqliteDataReader reader, out ContentBlockRecord record)
    {
        var userStr = reader.GetString(0);
        var mediaStr = reader.GetString(1);
        var mediaType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var mediaName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var createdStr = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

        if (Guid.TryParse(userStr, out var userId) && Guid.TryParse(mediaStr, out var mediaId))
        {
            if (!DateTime.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAt))
            {
                createdAt = DateTime.UtcNow;
            }

            record = new ContentBlockRecord(userId, mediaId, mediaType, mediaName, createdAt);
            return true;
        }

        record = default!;
        return false;
    }

    private static bool TryReadUserSettings(SqliteDataReader reader, out UserBlockSettings settings)
    {
        var userStr = reader.GetString(0);
        var isEnabled = reader.GetInt32(1) != 0;
        var disabledUntilStr = reader.IsDBNull(2) ? null : reader.GetString(2);
        var magicPlaylistStr = reader.IsDBNull(3) ? null : reader.GetString(3);
        var updatedStr = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

        if (Guid.TryParse(userStr, out var userId))
        {
            DateTime? disabledUntil = null;
            if (!string.IsNullOrWhiteSpace(disabledUntilStr) &&
                DateTime.TryParse(disabledUntilStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedDisabled))
            {
                disabledUntil = parsedDisabled;
            }

            Guid? magicPlaylistId = null;
            if (!string.IsNullOrWhiteSpace(magicPlaylistStr) && Guid.TryParse(magicPlaylistStr, out var parsedPlaylist))
            {
                magicPlaylistId = parsedPlaylist;
            }

            if (!DateTime.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updatedAt))
            {
                updatedAt = DateTime.UtcNow;
            }

            settings = new UserBlockSettings(userId, isEnabled, disabledUntil, magicPlaylistId, updatedAt);
            return true;
        }

        settings = default!;
        return false;
    }

    private void EnsureInitializedCore()
    {
        var dbPath = DatabaseFilePath;
        if (string.Equals(_initializedPath, dbPath, StringComparison.Ordinal) && File.Exists(dbPath))
        {
            return;
        }

        var parentDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        using var connection = new SqliteConnection(BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = PragmaAndSchemaSql;
        command.ExecuteNonQuery();
        _initializedPath = dbPath;
    }

    private SqliteConnection OpenConnectionCore()
    {
        EnsureInitializedCore();
        var connection = new SqliteConnection(BuildConnectionString(DatabaseFilePath));
        connection.Open();
        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL;";
        pragmaCommand.ExecuteNonQuery();
        return connection;
    }

    private static string BuildConnectionString(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        return builder.ToString();
    }
}
