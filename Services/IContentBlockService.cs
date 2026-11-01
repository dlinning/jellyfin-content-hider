using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// High-level service for managing per-user content blocks and toggle states backed directly by each user's Magic Playlist.
/// </summary>
public interface IContentBlockService
{
    /// <summary>
    /// Determines whether content blocking is currently active for the specified user.
    /// Automatically re-enables blocking if a timed pause has expired.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns><c>true</c> if blocking is active; <c>false</c> if paused/toggled off.</returns>
    bool IsBlockingActive(Guid userId);

    /// <summary>
    /// Sets whether content blocking is enabled for the user, with an optional pause duration in minutes.
    /// Synchronizes with the user's Magic Playlist Favorite state.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="enabled">Whether blocking should be active.</param>
    /// <param name="pauseDurationMinutes">Optional duration in minutes when pausing (<c>enabled == false</c>). Null uses configured default; &lt;= 0 means manual re-enable only.</param>
    void SetBlockingEnabled(Guid userId, bool enabled, int? pauseDurationMinutes = null);

    /// <summary>
    /// Associates a provisioned Magic Playlist identifier with the user in memory.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="playlistId">The Magic Playlist identifier.</param>
    void SetMagicPlaylistId(Guid userId, Guid playlistId);

    /// <summary>
    /// Gets the Magic Playlist identifier for the user (resolved by the <c>__is_content_hider_playlist</c> tag or cached ID), if present.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The Magic Playlist identifier, or <c>null</c>.</returns>
    Guid? GetMagicPlaylistId(Guid userId);

    /// <summary>
    /// Attempts to add a media item (Movie, Series, or BoxSet) to the user's Magic Playlist.
    /// Rejects single episodes, seasons, or unsupported item types.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="mediaId">The media item identifier to hide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the item is a valid show/movie/collection and was added (or already present); <c>false</c> if rejected.</returns>
    Task<bool> TryAddBlockAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a blocked media item from the user's Magic Playlist.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="mediaId">The media item identifier to unhide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if a block was removed; otherwise, <c>false</c>.</returns>
    Task<bool> RemoveBlockAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the directly hidden media items currently in the user's Magic Playlist.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The list of directly hidden <see cref="BaseItem"/> entities.</returns>
    IReadOnlyList<BaseItem> GetBlockedItems(Guid userId);

    /// <summary>
    /// Gets the expanded set of blocked item GUIDs for a user (including child seasons/episodes of hidden Series).
    /// Returns an empty set if blocking is currently paused for the user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The expanded set of blocked GUIDs.</returns>
    IReadOnlySet<Guid> GetExpandedBlockedIds(Guid userId);

    /// <summary>
    /// Determines whether a <see cref="BaseItemDto"/> (or any of its parent Series/Season/BoxSet containers) is blocked for the user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="dto">The item DTO to evaluate.</param>
    /// <param name="expandedBlockedIds">Optional pre-fetched expanded blocked IDs for the user.</param>
    /// <returns><c>true</c> if the DTO should be suppressed; otherwise, <c>false</c>.</returns>
    bool IsDtoBlocked(Guid userId, BaseItemDto dto, IReadOnlySet<Guid>? expandedBlockedIds = null);

    /// <summary>
    /// Determines whether a <see cref="SearchHint"/> (or its parent Series/Season) is blocked for the user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="hint">The search hint to evaluate.</param>
    /// <param name="expandedBlockedIds">Optional pre-fetched expanded blocked IDs for the user.</param>
    /// <returns><c>true</c> if the search hint should be suppressed; otherwise, <c>false</c>.</returns>
    bool IsSearchHintBlocked(Guid userId, SearchHint hint, IReadOnlySet<Guid>? expandedBlockedIds = null);

    /// <summary>
    /// Checks all users for expired pause timers and re-enables blocking, returning the user IDs whose timers expired.
    /// </summary>
    /// <returns>List of user IDs whose timed pause expired during this check.</returns>
    IReadOnlyList<Guid> ProcessExpiredTimers();

    /// <summary>
    /// Evicts inactive user caches from memory when no pause timer is pending.
    /// </summary>
    /// <param name="idleThreshold">Optional idle duration threshold. Defaults to 30 minutes.</param>
    /// <returns>The number of inactive user sessions evicted from memory.</returns>
    int EvictInactiveSessions(TimeSpan? idleThreshold = null);

    /// <summary>
    /// Removes cached session state for a deleted user.
    /// </summary>
    /// <param name="userId">The deleted user identifier.</param>
    void PurgeUserData(Guid userId);
}
