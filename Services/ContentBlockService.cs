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
/// Coordinates per-user content blocks and toggle states with write-through isolated JSON persistence,
/// per-user thread-safe locking, lazy on-demand loading, and automatic inactive session RAM eviction.
/// </summary>
public sealed class ContentBlockService : IContentBlockService
{
    private static readonly HashSet<Guid> EmptyGuidSet = [];

    /// <summary>
    /// Default inactivity duration before an idle user session is evicted from in-memory cache (30 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromMinutes(30);

    private readonly IContentBlockRepository _repository;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly Func<DateTime> _utcNowProvider;

    private readonly ConcurrentDictionary<Guid, UserSessionState> _userSessions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentBlockService"/> class for DI.
    /// </summary>
    /// <param name="repository">The content block repository.</param>
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
    /// <param name="repository">The content block repository.</param>
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

        _repository.EnsureInitialized();
    }

    /// <inheritdoc />
    public string DatabaseFilePath => _repository.DatabaseFilePath;

    /// <inheritdoc />
    public void ReloadFromDisk()
    {
        _repository.EnsureInitialized();
        _userSessions.Clear();
    }

    /// <inheritdoc />
    public int EvictInactiveSessions(TimeSpan? idleThreshold = null)
    {
        var threshold = idleThreshold ?? DefaultInactivityTimeout;
        var now = _utcNowProvider();
        var evictedCount = 0;

        foreach (var (userId, session) in _userSessions)
        {
            if (now - session.LastAccessedUtc > threshold)
            {
                if (_userSessions.TryRemove(userId, out _))
                {
                    evictedCount++;
                }
            }
        }

        return evictedCount;
    }

    /// <inheritdoc />
    public bool IsBlockingActive(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() =>
        {
            var settings = session.Settings;
            if (settings.IsBlockingEnabled)
            {
                return true;
            }

            var now = _utcNowProvider();
            if (settings.DisabledUntilUtc.HasValue && settings.DisabledUntilUtc.Value <= now)
            {
                // Pause timer expired: persist re-enabled state to disk immediately.
                SetBlockingEnabledInternal(session, true, null);
                return true;
            }

            return false;
        });
    }

    /// <inheritdoc />
    public UserBlockSettings GetUserSettings(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return new UserBlockSettings(Guid.Empty, true, null, null, _utcNowProvider());
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() =>
        {
            _ = IsBlockingActiveInternal(session);
            return session.Settings;
        });
    }

    /// <inheritdoc />
    public UserBlockSettings SetBlockingEnabled(Guid userId, bool enabled, int? pauseDurationMinutes = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() => SetBlockingEnabledInternal(session, enabled, pauseDurationMinutes));
    }

    /// <inheritdoc />
    public void SetMagicPlaylistId(Guid userId, Guid playlistId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        var session = GetOrLoadSession(userId);
        session.Execute(() =>
        {
            var now = _utcNowProvider();
            var existing = session.Settings;
            var updated = new UserBlockSettings(
                userId,
                existing.IsBlockingEnabled,
                existing.DisabledUntilUtc,
                playlistId,
                now);

            _repository.UpsertUserSettings(updated);
            session.Settings = updated;
            session.LastDiskWriteUtc = GetUserFileTimestamp(userId);
        });
    }

    /// <inheritdoc />
    public Guid? GetMagicPlaylistId(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() => session.Settings.MagicPlaylistId);
    }

    /// <inheritdoc />
    public ContentBlockRecord AddBlock(Guid userId, Guid mediaId, bool exactItemOnly = false)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() =>
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

            session.DirectBlocks[targetId] = record;
            session.ExpandedBlockedIds = null;
            session.LastDiskWriteUtc = GetUserFileTimestamp(userId);
            return record;
        });
    }

    /// <inheritdoc />
    public bool RemoveBlock(Guid userId, Guid mediaId)
    {
        if (userId == Guid.Empty || mediaId == Guid.Empty)
        {
            return false;
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() =>
        {
            var removed = _repository.RemoveBlock(userId, mediaId);
            if (!removed)
            {
                // If caller passed an Episode or Season ID whose parent Series was blocked, check parent Series.
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

            if (removed)
            {
                session.DirectBlocks.Remove(mediaId);
                session.ExpandedBlockedIds = null;
                session.LastDiskWriteUtc = GetUserFileTimestamp(userId);
            }

            return removed;
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return [];
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() => session.DirectBlocks.Values.OrderByDescending(b => b.CreatedAtUtc).ToList());
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetExpandedBlockedIds(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return EmptyGuidSet;
        }

        var session = GetOrLoadSession(userId);
        return session.Execute(() =>
        {
            if (!IsBlockingActiveInternal(session))
            {
                return EmptyGuidSet;
            }

            if (session.ExpandedBlockedIds is not null)
            {
                return session.ExpandedBlockedIds;
            }

            if (session.DirectBlocks.Count == 0)
            {
                session.ExpandedBlockedIds = EmptyGuidSet;
                return EmptyGuidSet;
            }

            var expanded = new HashSet<Guid>(session.DirectBlocks.Keys);

            // Expand descendants (Seasons & Episodes) for blocked Series/Seasons/Folders.
            var ancestorIds = session.DirectBlocks.Keys.ToArray();
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

            session.ExpandedBlockedIds = expanded;
            return expanded;
        });
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

        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            if (item is Episode ep && ep.SeriesId != Guid.Empty && blockedIds.Contains(ep.SeriesId))
            {
                return true;
            }

            if (item is Season s && s.SeriesId != Guid.Empty && blockedIds.Contains(s.SeriesId))
            {
                return true;
            }

            if (item.GetBaseItemKind() == BaseItemKind.BoxSet && _configProvider().HideEmptyCollections)
            {
                if (IsBoxSetCompletelyBlocked(itemId, blockedIds))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> ProcessExpiredTimers()
    {
        // Periodically evict idle sessions from RAM during the timer tick
        EvictInactiveSessions();

        var now = _utcNowProvider();
        var expiredUsers = new List<Guid>();

        // Check both cached and persisted user settings across all users
        foreach (var settings in _repository.GetAllUserSettings())
        {
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
        _userSessions.TryRemove(userId, out _);
        _repository.DeleteAllUserData(userId);
    }

    private UserSessionState GetOrLoadSession(Guid userId)
    {
        var session = _userSessions.GetOrAdd(userId, static id => new UserSessionState(id));

        return session.Execute(() =>
        {
            var currentWriteUtc = GetUserFileTimestamp(userId);

            if (!session.IsLoaded || currentWriteUtc != session.LastDiskWriteUtc)
            {
                var blocks = _repository.GetUserBlocks(userId);
                var settings = _repository.GetUserSettings(userId) ??
                               new UserBlockSettings(userId, true, null, null, _utcNowProvider());

                session.DirectBlocks = blocks.ToDictionary(b => b.MediaId);
                session.Settings = settings;
                session.ExpandedBlockedIds = null;
                session.LastDiskWriteUtc = currentWriteUtc;
                session.IsLoaded = true;
            }

            session.LastAccessedUtc = _utcNowProvider();
            return session;
        });
    }

    private DateTime GetUserFileTimestamp(Guid userId)
    {
        var path = Path.Combine(_repository.DatabaseFilePath, $"{userId:D}.json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private bool IsBlockingActiveInternal(UserSessionState session)
    {
        var settings = session.Settings;
        if (settings.IsBlockingEnabled)
        {
            return true;
        }

        var now = _utcNowProvider();
        if (settings.DisabledUntilUtc.HasValue && settings.DisabledUntilUtc.Value <= now)
        {
            SetBlockingEnabledInternal(session, true, null);
            return true;
        }

        return false;
    }

    private UserBlockSettings SetBlockingEnabledInternal(UserSessionState session, bool enabled, int? pauseDurationMinutes)
    {
        var now = _utcNowProvider();
        var existing = session.Settings;
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
            session.UserId,
            enabled,
            disabledUntil,
            existing.MagicPlaylistId,
            now);

        _repository.UpsertUserSettings(updated);
        session.Settings = updated;
        session.LastDiskWriteUtc = GetUserFileTimestamp(session.UserId);
        return updated;
    }

    private bool IsBoxSetCompletelyBlocked(Guid boxSetId, IReadOnlySet<Guid> blockedIds)
    {
        var children = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = boxSetId,
            Recursive = true
        });

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

    private sealed class UserSessionState
    {
        private readonly Lock _gate = new();

        public UserSessionState(Guid userId)
        {
            UserId = userId;
            Settings = new UserBlockSettings(userId, true, null, null, DateTime.UtcNow);
        }

        public Guid UserId { get; }

        public bool IsLoaded { get; set; }

        public Dictionary<Guid, ContentBlockRecord> DirectBlocks { get; set; } = [];

        public UserBlockSettings Settings { get; set; }

        public HashSet<Guid>? ExpandedBlockedIds { get; set; }

        public DateTime LastAccessedUtc { get; set; }

        public DateTime LastDiskWriteUtc { get; set; }

        public T Execute<T>(Func<T> action)
        {
            lock (_gate)
            {
                return action();
            }
        }

        public void Execute(Action action)
        {
            lock (_gate)
            {
                action();
            }
        }
    }
}
