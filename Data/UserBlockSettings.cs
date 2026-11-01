using System;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Represents a user's content-blocking toggle settings persisted in the <c>user_block_settings</c> SQLite table.
/// </summary>
/// <param name="UserId">The Jellyfin user identifier.</param>
/// <param name="IsBlockingEnabled">Whether content blocking is enabled for this user.</param>
/// <param name="DisabledUntilUtc">Optional UTC timestamp when a temporary pause automatically expires and blocking resumes.</param>
/// <param name="MagicPlaylistId">Optional identifier of the user's provisioned Magic Playlist.</param>
/// <param name="UpdatedAtUtc">The UTC timestamp when these settings were last updated.</param>
public sealed record UserBlockSettings(
    Guid UserId,
    bool IsBlockingEnabled,
    DateTime? DisabledUntilUtc,
    Guid? MagicPlaylistId,
    DateTime UpdatedAtUtc);
