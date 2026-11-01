using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Defines persistence operations against the standalone on-disk storage (<c>content_blocks.json</c>).
/// </summary>
public interface IContentBlockRepository
{
    /// <summary>
    /// Gets the resolved absolute path on disk to the storage file.
    /// </summary>
    string DatabaseFilePath { get; }

    /// <summary>
    /// Ensures the storage file and data structures exist on disk.
    /// </summary>
    void EnsureInitialized();

    /// <summary>
    /// Inserts or updates a content block row in <c>content_blocks</c> and commits it to disk.
    /// </summary>
    /// <param name="record">The block record to persist.</param>
    void UpsertBlock(ContentBlockRecord record);

    /// <summary>
    /// Removes a specific content block row for the given user and media item from disk.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="mediaId">The media item identifier.</param>
    /// <returns><c>true</c> if a row was deleted; otherwise, <c>false</c>.</returns>
    bool RemoveBlock(Guid userId, Guid mediaId);

    /// <summary>
    /// Gets all content block records for a specific user from disk.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The list of content block records for the user.</returns>
    IReadOnlyList<ContentBlockRecord> GetUserBlocks(Guid userId);

    /// <summary>
    /// Gets all content block records across all users from disk.
    /// </summary>
    /// <returns>All persisted content block records.</returns>
    IReadOnlyList<ContentBlockRecord> GetAllBlocks();

    /// <summary>
    /// Gets the persisted toggle and playlist settings for a user, or <c>null</c> if not yet stored.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The user's block settings if present.</returns>
    UserBlockSettings? GetUserSettings(Guid userId);

    /// <summary>
    /// Gets all persisted user block settings across all users from disk.
    /// </summary>
    /// <returns>All persisted user block settings.</returns>
    IReadOnlyList<UserBlockSettings> GetAllUserSettings();

    /// <summary>
    /// Inserts or updates a user's block settings on disk.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    void UpsertUserSettings(UserBlockSettings settings);

    /// <summary>
    /// Deletes all blocks and settings for a deleted Jellyfin user from disk.
    /// </summary>
    /// <param name="userId">The deleted user identifier.</param>
    /// <returns>The number of rows deleted.</returns>
    int DeleteAllUserData(Guid userId);

    /// <summary>
    /// Deletes all content blocks referencing a deleted media item across all users from disk.
    /// </summary>
    /// <param name="mediaId">The deleted media item identifier.</param>
    /// <returns>The number of rows deleted.</returns>
    int DeleteMediaBlocks(Guid mediaId);
}
