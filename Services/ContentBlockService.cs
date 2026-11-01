using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentHider.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Coordinates per-user content blocks and toggle states using each user's native Jellyfin Magic Playlist
/// (tagged with <c>__is_content_hider_playlist</c>) and <see cref="IUserDataManager"/> Favorite state.
/// </summary>
public sealed class ContentBlockService : IContentBlockService
{
    private static readonly HashSet<Guid> EmptyGuidSet = [];

    /// <summary>
    /// Default inactivity duration before an idle user session cache is evicted from memory (30 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromMinutes(30);

    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly Func<DateTime> _utcNowProvider;

    private readonly ConcurrentDictionary<Guid, UserSessionState> _userSessions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentBlockService"/> class for DI.
    /// </summary>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="userDataManager">The Jellyfin user data manager.</param>
    public ContentBlockService(
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
        : this(
            playlistManager,
            libraryManager,
            userManager,
            userDataManager,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentBlockService"/> class with custom providers for testing.
    /// </summary>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="userDataManager">The Jellyfin user data manager.</param>
    /// <param name="configProvider">Configuration provider delegate.</param>
    /// <param name="utcNowProvider">Clock delegate for deterministic timer testing.</param>
    public ContentBlockService(
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        Func<PluginConfiguration> configProvider,
        Func<DateTime> utcNowProvider)
    {
        ArgumentNullException.ThrowIfNull(playlistManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(utcNowProvider);

        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _configProvider = configProvider;
        _utcNowProvider = utcNowProvider;
    }

    /// <summary>
    /// Determines whether the specified <see cref="BaseItem"/> is a hideable type (Movie, Series, or BoxSet).
    /// Single episodes and seasons are not hideable.
    /// </summary>
    /// <param name="item">The item to evaluate.</param>
    /// <returns><c>true</c> if the item is a Movie, Series, or BoxSet; otherwise, <c>false</c>.</returns>
    public static bool IsHideableItem(BaseItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item is Episode or Season)
        {
            return false;
        }

        if (item is Movie or Series or BoxSet)
        {
            return true;
        }

        return item.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.BoxSet;
    }

    /// <inheritdoc />
    public int EvictInactiveSessions(TimeSpan? idleThreshold = null)
    {
        var threshold = idleThreshold ?? DefaultInactivityTimeout;
        var now = _utcNowProvider();
        var evictedCount = 0;

        foreach (var (userId, session) in _userSessions)
        {
            // Keep sessions that have an active countdown timer so expiration is tracked.
            if (session.DisabledUntilUtc.HasValue)
            {
                continue;
            }

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

        var session = GetSession(userId);
        return session.Execute(() => IsBlockingActiveInternal(session));
    }

    /// <inheritdoc />
    public void SetBlockingEnabled(Guid userId, bool enabled, int? pauseDurationMinutes = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        var session = GetSession(userId);
        session.Execute(() => SetBlockingEnabledInternal(session, enabled, pauseDurationMinutes));
    }

    /// <inheritdoc />
    public void SetMagicPlaylistId(Guid userId, Guid playlistId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        var session = GetSession(userId);
        session.Execute(() =>
        {
            session.MagicPlaylistId = playlistId;
            session.ExpandedBlockedIds = null;
        });
    }

    /// <inheritdoc />
    public Guid? GetMagicPlaylistId(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var session = GetSession(userId);
        return session.Execute(() => ResolveMagicPlaylistInternal(session)?.Id ?? session.MagicPlaylistId);
    }

    /// <inheritdoc />
    public async Task<bool> TryAddBlockAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        if (mediaId == Guid.Empty)
        {
            return false;
        }

        var item = _libraryManager.GetItemById(mediaId);
        if (!IsHideableItem(item))
        {
            return false;
        }

        var session = GetSession(userId);
        Playlist? playlistToUpdate = null;

        session.Execute(() =>
        {
            var playlist = ResolveMagicPlaylistInternal(session);
            if (playlist is null)
            {
                return;
            }

            var linked = (playlist.LinkedChildren ?? []).ToList();
            if (linked.All(lc => GetLinkedChildItemId(lc) != item!.Id))
            {
                linked.Add(new LinkedChild
                {
                    ItemId = item!.Id,
                    Type = LinkedChildType.Manual
                });
                playlist.LinkedChildren = [.. linked];
                playlistToUpdate = playlist;
            }

            session.ExpandedBlockedIds = null;
        });

        if (playlistToUpdate is not null)
        {
            _playlistManager.SavePlaylistFile(playlistToUpdate);
            await _libraryManager.UpdateItemAsync(
                playlistToUpdate,
                playlistToUpdate.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveBlockAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || mediaId == Guid.Empty)
        {
            return false;
        }

        var session = GetSession(userId);
        Playlist? playlistToUpdate = null;
        var removed = false;

        session.Execute(() =>
        {
            var playlist = ResolveMagicPlaylistInternal(session);
            if (playlist is null)
            {
                return;
            }

            var linked = (playlist.LinkedChildren ?? []).ToList();
            var removedCount = linked.RemoveAll(lc => GetLinkedChildItemId(lc) == mediaId);
            if (removedCount > 0)
            {
                playlist.LinkedChildren = [.. linked];
                playlistToUpdate = playlist;
                session.ExpandedBlockedIds = null;
                removed = true;
            }
        });

        if (playlistToUpdate is not null)
        {
            _playlistManager.SavePlaylistFile(playlistToUpdate);
            await _libraryManager.UpdateItemAsync(
                playlistToUpdate,
                playlistToUpdate.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetBlockedItems(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return [];
        }

        var session = GetSession(userId);
        return session.Execute(() =>
        {
            var playlist = ResolveMagicPlaylistInternal(session);
            if (playlist is null)
            {
                return [];
            }

            var items = new List<BaseItem>();
            var seen = new HashSet<Guid>();

            foreach (var lc in playlist.LinkedChildren ?? [])
            {
                var itemId = GetLinkedChildItemId(lc);
                if (itemId == Guid.Empty || !seen.Add(itemId))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (IsHideableItem(item))
                {
                    items.Add(item!);
                }
            }

            return items;
        });
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetExpandedBlockedIds(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return EmptyGuidSet;
        }

        var session = GetSession(userId);
        return session.Execute(() =>
        {
            if (!IsBlockingActiveInternal(session))
            {
                return EmptyGuidSet;
            }

            var playlist = ResolveMagicPlaylistInternal(session);
            if (playlist is null)
            {
                session.ExpandedBlockedIds = EmptyGuidSet;
                return EmptyGuidSet;
            }

            var directIds = GetDirectBlockedIdsFromPlaylist(playlist);
            var currentSignature = ComputeIdsSignature(directIds);

            if (session.ExpandedBlockedIds is not null && session.LastDirectIdsSignature == currentSignature)
            {
                return session.ExpandedBlockedIds;
            }

            if (directIds.Count == 0)
            {
                session.LastDirectIdsSignature = currentSignature;
                session.ExpandedBlockedIds = EmptyGuidSet;
                return EmptyGuidSet;
            }

            var expanded = new HashSet<Guid>(directIds);

            // Expand descendants (Seasons & Episodes) downward for blocked Series/BoxSets.
            var descendantIds = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                AncestorIds = [.. directIds],
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

            session.LastDirectIdsSignature = currentSignature;
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
        EvictInactiveSessions();

        var now = _utcNowProvider();
        var expiredUsers = new List<Guid>();

        foreach (var (userId, session) in _userSessions)
        {
            var expired = session.Execute(() =>
            {
                if (session.IsBlockingPaused &&
                    session.DisabledUntilUtc.HasValue &&
                    session.DisabledUntilUtc.Value <= now)
                {
                    SetBlockingEnabledInternal(session, true, null);
                    return true;
                }

                return false;
            });

            if (expired)
            {
                expiredUsers.Add(userId);
            }
        }

        return expiredUsers;
    }

    /// <inheritdoc />
    public void PurgeUserData(Guid userId)
    {
        _userSessions.TryRemove(userId, out _);
    }

    private static Guid GetLinkedChildItemId(LinkedChild? linkedChild)
    {
        return linkedChild?.ItemId is { } id && id != Guid.Empty
            ? id
            : Guid.Empty;
    }

    private static int ComputeIdsSignature(List<Guid> ids)
    {
        var hash = new HashCode();
        hash.Add(ids.Count);
        foreach (var id in ids)
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }

    private List<Guid> GetDirectBlockedIdsFromPlaylist(Playlist playlist)
    {
        var directIds = new List<Guid>();
        var seen = new HashSet<Guid>();

        foreach (var lc in playlist.LinkedChildren ?? [])
        {
            var itemId = GetLinkedChildItemId(lc);
            if (itemId == Guid.Empty || !seen.Add(itemId))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(itemId);

            // Never treat a single episode or season as hiding its parent series or being a valid block.
            if (item is Episode or Season)
            {
                continue;
            }

            if (item is null || IsHideableItem(item))
            {
                directIds.Add(itemId);
            }
        }

        return directIds;
    }

    private UserSessionState GetSession(Guid userId)
    {
        var session = _userSessions.GetOrAdd(userId, static id => new UserSessionState(id));
        session.LastAccessedUtc = _utcNowProvider();
        return session;
    }

    private Playlist? ResolveMagicPlaylistInternal(UserSessionState session)
    {
        if (session.MagicPlaylistId.HasValue && session.MagicPlaylistId.Value != Guid.Empty)
        {
            var byId = _playlistManager.GetPlaylistForUser(session.MagicPlaylistId.Value, session.UserId)
                       ?? _libraryManager.GetItemById(session.MagicPlaylistId.Value) as Playlist;
            if (byId is not null &&
                (MagicPlaylistService.HasManagedPlaylistTag(byId) || byId.Tags is null or { Length: 0 }))
            {
                return byId;
            }
        }

        var matchByTag = (_playlistManager.GetPlaylists(session.UserId) ?? [])
            .FirstOrDefault(p => p is not null && p.OwnerUserId == session.UserId && MagicPlaylistService.HasManagedPlaylistTag(p));

        if (matchByTag is not null)
        {
            session.MagicPlaylistId = matchByTag.Id;
            return matchByTag;
        }

        return null;
    }

    private bool IsBlockingActiveInternal(UserSessionState session)
    {
        var playlist = ResolveMagicPlaylistInternal(session);
        var user = _userManager.GetUserById(session.UserId);
        if (user is not null && playlist is not null)
        {
            var userData = _userDataManager.GetUserData(user, playlist);
            if (userData is not null)
            {
                if (userData.IsFavorite && !session.IsBlockingPaused)
                {
                    session.IsBlockingPaused = true;
                }
                else if (!userData.IsFavorite && session.IsBlockingPaused)
                {
                    session.IsBlockingPaused = false;
                    session.DisabledUntilUtc = null;
                }
            }
        }

        if (!session.IsBlockingPaused)
        {
            return true;
        }

        var now = _utcNowProvider();
        if (session.DisabledUntilUtc.HasValue && session.DisabledUntilUtc.Value <= now)
        {
            SetBlockingEnabledInternal(session, true, null);
            return true;
        }

        return false;
    }

    private void SetBlockingEnabledInternal(UserSessionState session, bool enabled, int? pauseDurationMinutes)
    {
        var now = _utcNowProvider();
        DateTime? disabledUntil = null;

        if (!enabled)
        {
            var effectiveMinutes = pauseDurationMinutes ?? _configProvider().DefaultPauseDurationMinutes;
            if (effectiveMinutes > 0)
            {
                disabledUntil = now.AddMinutes(effectiveMinutes);
            }
        }

        session.IsBlockingPaused = !enabled;
        session.DisabledUntilUtc = disabledUntil;

        var playlist = ResolveMagicPlaylistInternal(session);
        var user = _userManager.GetUserById(session.UserId);
        if (user is not null && playlist is not null)
        {
            var userData = _userDataManager.GetUserData(user, playlist);
            if (userData is not null && userData.IsFavorite != !enabled)
            {
                userData.IsFavorite = !enabled;
                _userDataManager.SaveUserData(user, playlist, userData, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
            }
        }
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
        }

        public Guid UserId { get; }

        public Guid? MagicPlaylistId { get; set; }

        public bool IsBlockingPaused { get; set; }

        public DateTime? DisabledUntilUtc { get; set; }

        public int LastDirectIdsSignature { get; set; }

        public HashSet<Guid>? ExpandedBlockedIds { get; set; }

        public DateTime LastAccessedUtc { get; set; }

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
