using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ContentHider.Data;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// High-level service for managing per-user content blocks, toggle states, and hierarchy suppression.
/// </summary>
public interface IContentBlockService
{
    /// <summary>
    /// Gets the resolved path to the on-disk storage file.
    /// </summary>
    string DatabaseFilePath { get; }

    /// <summary>
    /// Reloads all cached state from on-disk storage.
    /// </summary>
    void ReloadFromDisk();

    /// <summary>
    /// Determines whether content blocking is currently active for the specified user.
    /// Automatically re-enables blocking on disk if a timed pause has expired.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns><c>true</c> if blocking is active; <c>false</c> if paused/toggled off.</returns>
    bool IsBlockingActive(Guid userId);

    /// <summary>
    /// Gets the current toggle settings for the specified user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The user's block settings.</returns>
    UserBlockSettings GetUserSettings(Guid userId);

    /// <summary>
    /// Sets whether content blocking is enabled for the user, with an optional pause duration in minutes.
    /// Persists immediately to on-disk storage.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="enabled">Whether blocking should be active.</param>
    /// <param name="pauseDurationMinutes">Optional duration in minutes when pausing (<c>enabled == false</c>). Null or &lt;= 0 means manual re-enable only.</param>
    /// <returns>The updated user settings.</returns>
    UserBlockSettings SetBlockingEnabled(Guid userId, bool enabled, int? pauseDurationMinutes = null);

    /// <summary>
    /// Associates a provisioned Magic Playlist identifier with the user on disk.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="playlistId">The Magic Playlist identifier.</param>
    void SetMagicPlaylistId(Guid userId, Guid playlistId);

    /// <summary>
    /// Gets the Magic Playlist identifier for the user, if configured.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The Magic Playlist identifier, or <c>null</c>.</returns>
    Guid? GetMagicPlaylistId(Guid userId);

    /// <summary>
    /// Adds a content block for the specified user and media item, persisting immediately to disk.
    /// When episode normalization is enabled and <paramref name="exactItemOnly"/> is <c>false</c>,
    /// blocking an episode or season blocks its parent TV Series.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="mediaId">The media item identifier to block.</param>
    /// <param name="exactItemOnly">If <c>true</c>, does not normalize episodes/seasons to their parent series.</param>
    /// <returns>The persisted <see cref="ContentBlockRecord"/>.</returns>
    ContentBlockRecord AddBlock(Guid userId, Guid mediaId, bool exactItemOnly = false);

    /// <summary>
    /// Removes a content block for the specified user and media item from disk.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="mediaId">The media item identifier to unblock.</param>
    /// <returns><c>true</c> if a block was removed; otherwise, <c>false</c>.</returns>
    bool RemoveBlock(Guid userId, Guid mediaId);

    /// <summary>
    /// Gets the directly blocked records for the specified user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The list of blocked records.</returns>
    IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId);

    /// <summary>
    /// Gets the expanded set of blocked item GUIDs for a user (including child seasons/episodes and empty BoxSets).
    /// Returns an empty set if blocking is currently toggled off for the user.
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
    /// Checks all users for expired pause timers and re-enables blocking on disk, returning the user IDs whose timers expired.
    /// </summary>
    /// <returns>List of user IDs whose timed pause expired during this check.</returns>
    IReadOnlyList<Guid> ProcessExpiredTimers();

    /// <summary>
    /// Removes all blocks and settings for a deleted user.
    /// </summary>
    /// <param name="userId">The deleted user identifier.</param>
    void PurgeUserData(Guid userId);
}
