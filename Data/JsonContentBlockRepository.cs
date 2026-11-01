using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Standalone on-disk JSON repository that persists each user's content blocks and settings
/// into an isolated, per-user file (<c>users/{userId}.json</c>).
/// Eliminates multi-user lock contention and allows inactive users to be dropped from RAM.
/// </summary>
public sealed class JsonContentBlockRepository : IContentBlockRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string> _pathResolver;
    private readonly ConcurrentDictionary<Guid, object> _userFileLocks = new();
    private readonly object _globalInitLock = new();
    private bool _isInitialized;

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

            return Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.ContentHider", "users");
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonContentBlockRepository"/> class with an explicit directory path.
    /// </summary>
    /// <param name="storageDirectoryPath">The directory path where per-user JSON files are stored.</param>
    public JsonContentBlockRepository(string storageDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectoryPath);
        _pathResolver = () => storageDirectoryPath;
    }

    /// <inheritdoc />
    public string DatabaseFilePath => Path.GetFullPath(_pathResolver());

    /// <summary>
    /// Gets the absolute path on disk for the specified user's JSON configuration file.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The path to <c>{userId}.json</c>.</returns>
    public string GetUserFilePath(Guid userId)
    {
        return Path.Combine(DatabaseFilePath, $"{userId:D}.json");
    }

    /// <inheritdoc />
    public void EnsureInitialized()
    {
        lock (_globalInitLock)
        {
            if (_isInitialized)
            {
                return;
            }

            var dir = DatabaseFilePath;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Auto-migrate from legacy single content_blocks.json if it exists
            MigrateLegacySingleFileIfPresent(dir);

            _isInitialized = true;
        }
    }

    /// <inheritdoc />
    public void UpsertBlock(ContentBlockRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(record.UserId, static _ => new object());
        lock (lockObj)
        {
            var model = LoadUserModelCore(record.UserId);

            var existingIndex = model.Blocks.FindIndex(b => b.MediaId == record.MediaId);
            var mediaName = !string.IsNullOrWhiteSpace(record.MediaName)
                ? record.MediaName
                : (existingIndex >= 0 ? model.Blocks[existingIndex].MediaName : string.Empty);

            var toStore = record with { MediaName = mediaName };

            if (existingIndex >= 0)
            {
                model.Blocks[existingIndex] = toStore;
            }
            else
            {
                model.Blocks.Add(toStore);
            }

            SaveUserModelCore(model);
        }
    }

    /// <inheritdoc />
    public bool RemoveBlock(Guid userId, Guid mediaId)
    {
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
        lock (lockObj)
        {
            var filePath = GetUserFilePath(userId);
            if (!File.Exists(filePath))
            {
                return false;
            }

            var model = LoadUserModelCore(userId);
            var removedCount = model.Blocks.RemoveAll(b => b.MediaId == mediaId);
            if (removedCount > 0)
            {
                SaveUserModelCore(model);
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId)
    {
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
        lock (lockObj)
        {
            var model = LoadUserModelCore(userId);
            return model.Blocks
                .OrderByDescending(b => b.CreatedAtUtc)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetAllBlocks()
    {
        EnsureInitialized();

        var results = new List<ContentBlockRecord>();
        var userFiles = GetUserFilesSafe();

        foreach (var file in userFiles)
        {
            if (TryParseUserIdFromFileName(file, out var userId))
            {
                var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
                lock (lockObj)
                {
                    var model = LoadUserModelFromFile(file, userId);
                    results.AddRange(model.Blocks);
                }
            }
        }

        return results.OrderByDescending(b => b.CreatedAtUtc).ToList();
    }

    /// <inheritdoc />
    public UserBlockSettings? GetUserSettings(Guid userId)
    {
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
        lock (lockObj)
        {
            var model = LoadUserModelCore(userId);
            return model.Settings;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UserBlockSettings> GetAllUserSettings()
    {
        EnsureInitialized();

        var results = new List<UserBlockSettings>();
        var userFiles = GetUserFilesSafe();

        foreach (var file in userFiles)
        {
            if (TryParseUserIdFromFileName(file, out var userId))
            {
                var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
                lock (lockObj)
                {
                    var model = LoadUserModelFromFile(file, userId);
                    if (model.Settings is not null)
                    {
                        results.Add(model.Settings);
                    }
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void UpsertUserSettings(UserBlockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(settings.UserId, static _ => new object());
        lock (lockObj)
        {
            var model = LoadUserModelCore(settings.UserId);

            var magicPlaylistId = settings.MagicPlaylistId ?? model.Settings?.MagicPlaylistId;
            var toStore = settings with { MagicPlaylistId = magicPlaylistId };

            model.Settings = toStore;
            SaveUserModelCore(model);
        }
    }

    /// <inheritdoc />
    public int DeleteAllUserData(Guid userId)
    {
        EnsureInitialized();

        var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
        lock (lockObj)
        {
            var filePath = GetUserFilePath(userId);
            if (!File.Exists(filePath))
            {
                return 0;
            }

            var model = LoadUserModelCore(userId);
            var deletedCount = model.Blocks.Count + (model.Settings is not null ? 1 : 0);

            try
            {
                File.Delete(filePath);
            }
            catch (IOException)
            {
                // In case of transient IO lock, clear content
                model.Blocks.Clear();
                model.Settings = null;
                SaveUserModelCore(model);
            }

            _userFileLocks.TryRemove(userId, out _);
            return deletedCount;
        }
    }

    /// <inheritdoc />
    public int DeleteMediaBlocks(Guid mediaId)
    {
        EnsureInitialized();

        var totalDeleted = 0;
        var userFiles = GetUserFilesSafe();

        foreach (var file in userFiles)
        {
            if (TryParseUserIdFromFileName(file, out var userId))
            {
                var lockObj = _userFileLocks.GetOrAdd(userId, static _ => new object());
                lock (lockObj)
                {
                    var model = LoadUserModelFromFile(file, userId);
                    var removed = model.Blocks.RemoveAll(b => b.MediaId == mediaId);
                    if (removed > 0)
                    {
                        totalDeleted += removed;
                        SaveUserModelCore(model);
                    }
                }
            }
        }

        return totalDeleted;
    }

    private UserStorageModel LoadUserModelCore(Guid userId)
    {
        var filePath = GetUserFilePath(userId);
        return LoadUserModelFromFile(filePath, userId);
    }

    private static UserStorageModel LoadUserModelFromFile(string filePath, Guid userId)
    {
        if (!File.Exists(filePath))
        {
            return new UserStorageModel { UserId = userId };
        }

        try
        {
            var json = File.ReadAllText(filePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var model = JsonSerializer.Deserialize<UserStorageModel>(json, JsonOptions);
                if (model is not null)
                {
                    model.UserId = userId;
                    model.Blocks ??= [];
                    return model;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt file fallback
        }
        catch (IOException)
        {
            // Transient IO read fallback
        }

        return new UserStorageModel { UserId = userId };
    }

    private void SaveUserModelCore(UserStorageModel model)
    {
        var filePath = GetUserFilePath(model.UserId);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(model, JsonOptions);
        var tempFile = Path.Combine(dir ?? ".", $"{model.UserId:D}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempFile, json);
        File.Move(tempFile, filePath, overwrite: true);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Filesystem enumeration failures should return empty list rather than crashing.")]
    private string[] GetUserFilesSafe()
    {
        var dir = DatabaseFilePath;
        if (!Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool TryParseUserIdFromFileName(string filePath, out Guid userId)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        return Guid.TryParse(fileNameWithoutExt, out userId);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort legacy migration.")]
    private void MigrateLegacySingleFileIfPresent(string usersDir)
    {
        var parentDir = Path.GetDirectoryName(usersDir);
        if (string.IsNullOrEmpty(parentDir))
        {
            return;
        }

        var legacyFile = Path.Combine(parentDir, "content_blocks.json");
        if (!File.Exists(legacyFile))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(legacyFile);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var legacy = JsonSerializer.Deserialize<LegacyStorageModel>(json, JsonOptions);
                if (legacy is not null)
                {
                    var blocksByUser = (legacy.Blocks ?? [])
                        .Where(b => b is not null && b.UserId != Guid.Empty)
                        .GroupBy(b => b.UserId)
                        .ToList();

                    var settingsByUser = (legacy.Settings ?? [])
                        .Where(s => s is not null && s.UserId != Guid.Empty)
                        .ToDictionary(s => s.UserId);

                    var allUserIds = new HashSet<Guid>(blocksByUser.Select(g => g.Key));
                    allUserIds.UnionWith(settingsByUser.Keys);

                    foreach (var userId in allUserIds)
                    {
                        var userModel = new UserStorageModel
                        {
                            Version = 1,
                            UserId = userId,
                            Settings = settingsByUser.GetValueOrDefault(userId),
                            Blocks = blocksByUser.FirstOrDefault(g => g.Key == userId)?.ToList() ?? []
                        };

                        SaveUserModelCore(userModel);
                    }
                }
            }

            File.Move(legacyFile, $"{legacyFile}.migrated", overwrite: true);
        }
        catch (Exception)
        {
            // Non-critical migration failure
        }
    }

    private sealed class UserStorageModel
    {
        public int Version { get; set; } = 1;

        public Guid UserId { get; set; }

        public UserBlockSettings? Settings { get; set; }

        public List<ContentBlockRecord> Blocks { get; set; } = [];
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated via System.Text.Json deserialization")]
    private sealed class LegacyStorageModel
    {
        public int Version { get; set; } = 1;

        public List<ContentBlockRecord>? Blocks { get; set; }

        public List<UserBlockSettings>? Settings { get; set; }
    }
}
