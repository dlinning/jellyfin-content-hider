using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Standalone on-disk JSON repository for persisting per-user content blocks and toggle states.
/// Completely self-contained with zero third-party database dependencies.
/// </summary>
public sealed class JsonContentBlockRepository : IContentBlockRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _syncLock = new();
    private readonly Func<string> _pathResolver;
    private readonly Dictionary<Guid, Dictionary<Guid, ContentBlockRecord>> _blocks = new();
    private readonly Dictionary<Guid, UserBlockSettings> _settings = new();

    private string? _cachedPath;
    private DateTime _lastReadWriteUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonContentBlockRepository"/> class for DI.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    public JsonContentBlockRepository(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _pathResolver = () =>
        {
            if (Plugin.Instance is { } plugin)
            {
                return plugin.DatabaseFilePath;
            }

            return Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.ContentHider", "content_blocks.json");
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonContentBlockRepository"/> class with an explicit file path.
    /// </summary>
    /// <param name="databaseFilePath">The explicit file path for the on-disk storage file.</param>
    public JsonContentBlockRepository(string databaseFilePath)
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
            EnsureLoadedCore();
        }
    }

    /// <inheritdoc />
    public void UpsertBlock(ContentBlockRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_syncLock)
        {
            EnsureLoadedCore();

            if (!_blocks.TryGetValue(record.UserId, out var userMap))
            {
                userMap = new Dictionary<Guid, ContentBlockRecord>();
                _blocks[record.UserId] = userMap;
            }

            var mediaName = !string.IsNullOrWhiteSpace(record.MediaName)
                ? record.MediaName
                : (userMap.TryGetValue(record.MediaId, out var existing) ? existing.MediaName : string.Empty);

            var toStore = record with { MediaName = mediaName };
            userMap[record.MediaId] = toStore;

            SaveCore();
        }
    }

    /// <inheritdoc />
    public bool RemoveBlock(Guid userId, Guid mediaId)
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            if (_blocks.TryGetValue(userId, out var userMap) && userMap.Remove(mediaId))
            {
                if (userMap.Count == 0)
                {
                    _blocks.Remove(userId);
                }

                SaveCore();
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId)
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            if (_blocks.TryGetValue(userId, out var userMap))
            {
                return userMap.Values
                    .OrderByDescending(b => b.CreatedAtUtc)
                    .ToList();
            }

            return [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetAllBlocks()
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            return _blocks.Values
                .SelectMany(userMap => userMap.Values)
                .OrderByDescending(b => b.CreatedAtUtc)
                .ToList();
        }
    }

    /// <inheritdoc />
    public UserBlockSettings? GetUserSettings(Guid userId)
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            return _settings.TryGetValue(userId, out var settings) ? settings : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UserBlockSettings> GetAllUserSettings()
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            return _settings.Values.ToList();
        }
    }

    /// <inheritdoc />
    public void UpsertUserSettings(UserBlockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncLock)
        {
            EnsureLoadedCore();

            var magicPlaylistId = settings.MagicPlaylistId;
            if (magicPlaylistId is null && _settings.TryGetValue(settings.UserId, out var existing))
            {
                magicPlaylistId = existing.MagicPlaylistId;
            }

            var toStore = settings with { MagicPlaylistId = magicPlaylistId };
            _settings[settings.UserId] = toStore;

            SaveCore();
        }
    }

    /// <inheritdoc />
    public int DeleteAllUserData(Guid userId)
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            var deleted = 0;
            if (_blocks.TryGetValue(userId, out var userMap))
            {
                deleted += userMap.Count;
                _blocks.Remove(userId);
            }

            if (_settings.Remove(userId))
            {
                deleted++;
            }

            if (deleted > 0)
            {
                SaveCore();
            }

            return deleted;
        }
    }

    /// <inheritdoc />
    public int DeleteMediaBlocks(Guid mediaId)
    {
        lock (_syncLock)
        {
            EnsureLoadedCore();

            var deleted = 0;
            var emptyUsers = new List<Guid>();

            foreach (var (userId, userMap) in _blocks)
            {
                if (userMap.Remove(mediaId))
                {
                    deleted++;
                    if (userMap.Count == 0)
                    {
                        emptyUsers.Add(userId);
                    }
                }
            }

            foreach (var userId in emptyUsers)
            {
                _blocks.Remove(userId);
            }

            if (deleted > 0)
            {
                SaveCore();
            }

            return deleted;
        }
    }

    private void EnsureLoadedCore()
    {
        var filePath = DatabaseFilePath;

        // If path changed or file modified externally, reload
        if (!string.Equals(_cachedPath, filePath, StringComparison.Ordinal) ||
            (File.Exists(filePath) && File.GetLastWriteTimeUtc(filePath) != _lastReadWriteUtc))
        {
            LoadCore(filePath);
        }
    }

    private void LoadCore(string filePath)
    {
        _cachedPath = filePath;
        _blocks.Clear();
        _settings.Clear();

        if (!File.Exists(filePath))
        {
            var parentDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            SaveCore();
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var model = JsonSerializer.Deserialize<JsonStorageModel>(json, JsonOptions);
                if (model is not null)
                {
                    if (model.Blocks is not null)
                    {
                        foreach (var record in model.Blocks)
                        {
                            if (record is null || record.UserId == Guid.Empty || record.MediaId == Guid.Empty)
                            {
                                continue;
                            }

                            if (!_blocks.TryGetValue(record.UserId, out var userMap))
                            {
                                userMap = new Dictionary<Guid, ContentBlockRecord>();
                                _blocks[record.UserId] = userMap;
                            }

                            userMap[record.MediaId] = record;
                        }
                    }

                    if (model.Settings is not null)
                    {
                        foreach (var setting in model.Settings)
                        {
                            if (setting is null || setting.UserId == Guid.Empty)
                            {
                                continue;
                            }

                            _settings[setting.UserId] = setting;
                        }
                    }
                }
            }

            _lastReadWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
        catch (JsonException)
        {
            // Corrupt file fallback: keep maps initialized and avoid crashing
            _lastReadWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
    }

    private void SaveCore()
    {
        var filePath = DatabaseFilePath;
        var parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var model = new JsonStorageModel
        {
            Version = 1,
            Blocks = _blocks.Values.SelectMany(u => u.Values).OrderByDescending(b => b.CreatedAtUtc).ToList(),
            Settings = _settings.Values.ToList()
        };

        var json = JsonSerializer.Serialize(model, JsonOptions);
        var tempFile = Path.Combine(parentDir ?? ".", $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempFile, json);
        File.Move(tempFile, filePath, overwrite: true);

        _lastReadWriteUtc = File.GetLastWriteTimeUtc(filePath);
    }

    private sealed class JsonStorageModel
    {
        public int Version { get; set; } = 1;

        public List<ContentBlockRecord> Blocks { get; set; } = [];

        public List<UserBlockSettings> Settings { get; set; } = [];
    }
}
