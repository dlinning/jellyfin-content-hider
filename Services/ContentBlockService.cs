using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Coordinates per-user content blocks and toggle states with write-through SQLite persistence
/// and external file-change synchronization.
/// </summary>
public sealed class ContentBlockService : IContentBlockService
{
    private static readonly HashSet<Guid> EmptyGuidSet = [];

    private readonly IContentBlockRepository _repository;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly Lock _stateLock = new();

    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, ContentBlockRecord>> _userBlocks = new();
    private readonly ConcurrentDictionary<Guid, UserBlockSettings> _userSettings = new();
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _expandedCache = new();
    private DateTime _lastKnownDiskWriteUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentBlockService"/> class for DI.
    /// </summary>
    /// <param name="repository">The SQLite content block repository.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    public ContentBlockService(
        IContentBlockRepository repository,
        ILibraryManager libraryManager)
        : this(
            repository,
            libraryManager,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentBlockService"/> class with custom providers for testing.
    /// </summary>
    /// <param name="repository">The SQLite content block repository.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="configProvider">Configuration provider delegate.</param>
    /// <param name="utcNowProvider">Clock delegate for deterministic timer testing.</param>
    public ContentBlockService(
        IContentBlockRepository repository,
        ILibraryManager libraryManager,
        Func<PluginConfiguration> configProvider,
        Func<DateTime> utcNowProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(utcNowProvider);

        _repository = repository;
        _libraryManager = libraryManager;
        _configProvider = configProvider;
        _utcNowProvider = utcNowProvider;

        ReloadFromDisk();
    }

    /// <inheritdoc />
    public string DatabaseFilePath => _repository.DatabaseFilePath;

    /// <inheritdoc />
    public void ReloadFromDisk()
    {
        lock (_stateLock)
        {
            _repository.EnsureInitialized();

            _userBlocks.Clear();
            _userSettings.Clear();
            _expandedCache.Clear();

            foreach (var block in _repository.GetAllBlocks())
            {
                var dict = _userBlocks.GetOrAdd(block.UserId, static _ => []);
                dict[block.MediaId] = block;
            }

            foreach (var settings in _repository.GetAllUserSettings())
            {
                _userSettings[settings.UserId] = settings;
            }

            UpdateLastDiskTimestampCore();
        }
    }

    /// <inheritdoc />
    public bool IsBlockingActive(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        EnsureDiskSynced();

        if (!_userSettings.TryGetValue(userId, out var settings))
        {
            return true;
        }

        if (settings.IsBlockingEnabled)
        {
            return true;
        }

        var now = _utcNowProvider();
        if (settings.DisabledUntilUtc.HasValue && settings.DisabledUntilUtc.Value <= now)
        {
            // Pause timer expired: persist re-enabled state to disk immediately.
            SetBlockingEnabled(userId, true);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public UserBlockSettings GetUserSettings(Guid userId)
    {
        EnsureDiskSynced();
        _ = IsBlockingActive(userId);

        if (_userSettings.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        return new UserBlockSettings(userId, true, null, null, _utcNowProvider());
    }

    /// <inheritdoc />
    public UserBlockSettings SetBlockingEnabled(Guid userId, bool enabled, int? pauseDurationMinutes = null)
    {
        lock (_stateLock)
        {
            var now = _utcNowProvider();
            var existing = _userSettings.GetValueOrDefault(userId);
            DateTime? disabledUntil = null;

            if (!enabled)
            {
                var effectiveMinutes = pauseDurationMinutes ?? _configProvider().DefaultPauseDurationMinutes;
                if (effectiveMinutes > 0)
                {
                    disabledUntil = now.AddMinutes(effectiveMinutes);
                }
            }

            var updated = new UserBlockSettings(
                userId,
                enabled,
                disabledUntil,
                existing?.MagicPlaylistId,
                now);

            _repository.UpsertUserSettings(updated);
            _userSettings[userId] = updated;
            UpdateLastDiskTimestampCore();
            return updated;
        }
    }

    /// <inheritdoc />
    public void SetMagicPlaylistId(Guid userId, Guid playlistId)
    {
        lock (_stateLock)
        {
            var now = _utcNowProvider();
            var existing = _userSettings.GetValueOrDefault(userId);
            var updated = new UserBlockSettings(
                userId,
                existing?.IsBlockingEnabled ?? true,
                existing?.DisabledUntilUtc,
                playlistId,
                now);

            _repository.UpsertUserSettings(updated);
            _userSettings[userId] = updated;
            UpdateLastDiskTimestampCore();
        }
    }

    /// <inheritdoc />
    public Guid? GetMagicPlaylistId(Guid userId)
    {
        EnsureDiskSynced();
        return _userSettings.TryGetValue(userId, out var settings) ? settings.MagicPlaylistId : null;
    }

    /// <inheritdoc />
    public ContentBlockRecord AddBlock(Guid userId, Guid mediaId, bool exactItemOnly = false)
    {
        lock (_stateLock)
        {
            var config = _configProvider();
            var targetId = mediaId;
            var mediaType = "Unknown";
            var mediaName = string.Empty;

            var item = _libraryManager.GetItemById(mediaId);
            if (item is not null)
            {
                if (!exactItemOnly && config.NormalizeEpisodesToSeries)
                {
                    if (item is Episode episode && episode.SeriesId != Guid.Empty)
                    {
                        var seriesItem = _libraryManager.GetItemById(episode.SeriesId);
                        if (seriesItem is not null)
                        {
                            item = seriesItem;
                        }
                        else
                        {
                            targetId = episode.SeriesId;
                            mediaType = nameof(BaseItemKind.Series);
                        }
                    }
                    else if (item is Season season && season.SeriesId != Guid.Empty)
                    {
                        var seriesItem = _libraryManager.GetItemById(season.SeriesId);
                        if (seriesItem is not null)
                        {
                            item = seriesItem;
                        }
                        else
                        {
                            targetId = season.SeriesId;
                            mediaType = nameof(BaseItemKind.Series);
                        }
                    }
                }

                if (targetId == mediaId || item.Id == targetId)
                {
                    targetId = item.Id;
                    mediaType = item.GetBaseItemKind().ToString();
                    mediaName = item.Name ?? string.Empty;
                }
            }

            var record = new ContentBlockRecord(userId, targetId, mediaType, mediaName, _utcNowProvider());
            _repository.UpsertBlock(record);

            var dict = _userBlocks.GetOrAdd(userId, static _ => []);
            dict[targetId] = record;
            _expandedCache.TryRemove(userId, out _);
            UpdateLastDiskTimestampCore();
            return record;
        }
    }

    /// <inheritdoc />
    public bool RemoveBlock(Guid userId, Guid mediaId)
    {
        lock (_stateLock)
        {
            var removed = _repository.RemoveBlock(userId, mediaId);
            if (!removed)
            {
                // If the caller passed an Episode or Season ID whose parent Series was blocked, check parent Series.
                var item = _libraryManager.GetItemById(mediaId);
                var parentSeriesId = item switch
                {
                    Episode ep => ep.SeriesId,
                    Season s => s.SeriesId,
                    _ => Guid.Empty
                };

                if (parentSeriesId != Guid.Empty)
                {
                    mediaId = parentSeriesId;
                    removed = _repository.RemoveBlock(userId, mediaId);
                }
            }

            if (removed && _userBlocks.TryGetValue(userId, out var dict))
            {
                dict.Remove(mediaId);
            }

            _expandedCache.TryRemove(userId, out _);
            UpdateLastDiskTimestampCore();
            return removed;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId)
    {
        EnsureDiskSynced();
        if (_userBlocks.TryGetValue(userId, out var dict))
        {
            lock (_stateLock)
            {
                return dict.Values.OrderByDescending(b => b.CreatedAtUtc).ToList();
            }
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetExpandedBlockedIds(Guid userId)
    {
        if (!IsBlockingActive(userId))
        {
            return EmptyGuidSet;
        }

        if (_expandedCache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        lock (_stateLock)
        {
            if (!_userBlocks.TryGetValue(userId, out var directDict) || directDict.Count == 0)
            {
                return EmptyGuidSet;
            }

            var expanded = new HashSet<Guid>(directDict.Keys);

            // Expand descendants (Seasons & Episodes) for blocked Series/Seasons/Folders.
            var ancestorIds = directDict.Keys.ToArray();
            var descendantIds = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                AncestorIds = ancestorIds,
                Recursive = true
            });

            if (descendantIds is not null)
            {
                foreach (var childId in descendantIds)
                {
                    if (childId != Guid.Empty)
                    {
                        expanded.Add(childId);
                    }
                }
            }

            _expandedCache[userId] = expanded;
            return expanded;
        }
    }

    /// <inheritdoc />
    public bool IsDtoBlocked(Guid userId, BaseItemDto dto, IReadOnlySet<Guid>? expandedBlockedIds = null)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var blockedIds = expandedBlockedIds ?? GetExpandedBlockedIds(userId);
        if (blockedIds.Count == 0)
        {
            return false;
        }

        if (dto.Id != Guid.Empty && blockedIds.Contains(dto.Id))
        {
            return true;
        }

        if (dto.SeriesId.HasValue && dto.SeriesId.Value != Guid.Empty && blockedIds.Contains(dto.SeriesId.Value))
        {
            return true;
        }

        if (dto.SeasonId.HasValue && dto.SeasonId.Value != Guid.Empty && blockedIds.Contains(dto.SeasonId.Value))
        {
            return true;
        }

        if (dto.ParentId.HasValue && dto.ParentId.Value != Guid.Empty && blockedIds.Contains(dto.ParentId.Value))
        {
            return true;
        }

        if (dto.Type == BaseItemKind.BoxSet && _configProvider().HideEmptyCollections && dto.Id != Guid.Empty)
        {
            if (IsBoxSetCompletelyBlocked(dto.Id, blockedIds))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsSearchHintBlocked(Guid userId, SearchHint hint, IReadOnlySet<Guid>? expandedBlockedIds = null)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var blockedIds = expandedBlockedIds ?? GetExpandedBlockedIds(userId);
        if (blockedIds.Count == 0)
        {
            return false;
        }

        var itemId = hint.Id;
        if (itemId != Guid.Empty && blockedIds.Contains(itemId))
        {
            return true;
        }

        if (itemId != Guid.Empty)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is Episode ep &&
                ((ep.SeriesId != Guid.Empty && blockedIds.Contains(ep.SeriesId)) ||
                 (ep.SeasonId != Guid.Empty && blockedIds.Contains(ep.SeasonId))))
            {
                return true;
            }

            if (item is Season season && season.SeriesId != Guid.Empty && blockedIds.Contains(season.SeriesId))
            {
                return true;
            }

            if (item is not null && item.ParentId != Guid.Empty && blockedIds.Contains(item.ParentId))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> ProcessExpiredTimers()
    {
        EnsureDiskSynced();
        var now = _utcNowProvider();
        var expiredUsers = new List<Guid>();

        foreach (var kvp in _userSettings)
        {
            var settings = kvp.Value;
            if (!settings.IsBlockingEnabled &&
                settings.DisabledUntilUtc.HasValue &&
                settings.DisabledUntilUtc.Value <= now)
            {
                SetBlockingEnabled(settings.UserId, true);
                expiredUsers.Add(settings.UserId);
            }
        }

        return expiredUsers;
    }

    /// <inheritdoc />
    public void PurgeUserData(Guid userId)
    {
        lock (_stateLock)
        {
            _repository.DeleteAllUserData(userId);
            _userBlocks.TryRemove(userId, out _);
            _userSettings.TryRemove(userId, out _);
            _expandedCache.TryRemove(userId, out _);
            UpdateLastDiskTimestampCore();
        }
    }

    private bool IsBoxSetCompletelyBlocked(Guid boxSetId, IReadOnlySet<Guid> blockedIds)
    {
        if (_libraryManager.GetItemById(boxSetId) is not BoxSet boxSet)
        {
            return false;
        }

        var children = boxSet.GetLinkedChildren();
        if (children is null || children.Count == 0)
        {
            return false;
        }

        var hasAnyChild = false;
        foreach (var child in children)
        {
            if (child is null || child.Id == Guid.Empty)
            {
                continue;
            }

            hasAnyChild = true;
            if (!blockedIds.Contains(child.Id))
            {
                return false;
            }
        }

        return hasAnyChild;
    }

    private void EnsureDiskSynced()
    {
        var path = _repository.DatabaseFilePath;
        if (!File.Exists(path))
        {
            ReloadFromDisk();
            return;
        }

        var currentWriteUtc = File.GetLastWriteTimeUtc(path);
        if (currentWriteUtc != _lastKnownDiskWriteUtc)
        {
            ReloadFromDisk();
        }
    }

    private void UpdateLastDiskTimestampCore()
    {
        var path = _repository.DatabaseFilePath;
        if (File.Exists(path))
        {
            _lastKnownDiskWriteUtc = File.GetLastWriteTimeUtc(path);
        }
    }
}
