using System;

namespace Jellyfin.Plugin.ContentHider.Api;

/// <summary>
/// Request payload for toggling content blocking on or off.
/// </summary>
public sealed class ToggleBlockingRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether content blocking should be enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets an optional duration in minutes to pause blocking when <see cref="Enabled"/> is <c>false</c>.
    /// When <c>0</c>, blocking stays off until manually re-enabled.
    /// </summary>
    public int? DurationMinutes { get; set; }
}

/// <summary>
/// Response payload describing the calling user's current blocking status.
/// </summary>
/// <param name="UserId">The authenticated user ID.</param>
/// <param name="IsBlockingEnabled">Whether blocking is enabled in settings.</param>
/// <param name="IsEffectivelyBlocking">Whether blocking is currently active.</param>
/// <param name="DisabledUntilUtc">Optional UTC timestamp when a timed pause automatically expires.</param>
/// <param name="DefaultPauseDurationMinutes">The server's configured default pause duration in minutes.</param>
/// <param name="BlockedCount">The number of items directly blocked by this user.</param>
/// <param name="MagicPlaylistId">Optional ID of the user's Magic Playlist.</param>
public sealed record UserBlockingStatusDto(
    Guid UserId,
    bool IsBlockingEnabled,
    bool IsEffectivelyBlocking,
    DateTime? DisabledUntilUtc,
    int DefaultPauseDurationMinutes,
    int BlockedCount,
    Guid? MagicPlaylistId);

/// <summary>
/// Response DTO representing a blocked item or search item for the Content Hider UI.
/// </summary>
/// <param name="MediaId">The media item identifier.</param>
/// <param name="Name">The display name of the item.</param>
/// <param name="Type">The item kind (Movie, Series, BoxSet, etc.).</param>
/// <param name="ProductionYear">Optional release/production year.</param>
/// <param name="HasPrimaryImage">Whether the item has a primary poster image.</param>
/// <param name="IsBlocked">Whether this item is currently blocked for the user.</param>
/// <param name="CreatedAtUtc">When the block was created, if blocked.</param>
public sealed record ContentHiderItemDto(
    Guid MediaId,
    string Name,
    string Type,
    int? ProductionYear,
    bool HasPrimaryImage,
    bool IsBlocked,
    DateTime? CreatedAtUtc);

/// <summary>
/// Response DTO describing the on-disk storage status for the Admin Dashboard configuration page.
/// </summary>
/// <param name="DatabaseFilePath">The absolute path on disk to <c>content_blocks.json</c>.</param>
/// <param name="DatabaseFileExists">Whether the file currently exists on disk.</param>
/// <param name="DatabaseFileSizeBytes">The size of the storage file in bytes.</param>
/// <param name="TotalBlocksCount">Total number of persisted content blocks across all users.</param>
public sealed record AdminDatabaseInfoDto(
    string DatabaseFilePath,
    bool DatabaseFileExists,
    long DatabaseFileSizeBytes,
    int TotalBlocksCount);
