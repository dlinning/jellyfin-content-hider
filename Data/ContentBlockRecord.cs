using System;

namespace Jellyfin.Plugin.ContentHider.Data;

/// <summary>
/// Represents a single content block row in the <c>content_blocks</c> SQLite table.
/// </summary>
/// <param name="UserId">The Jellyfin user identifier.</param>
/// <param name="MediaId">The blocked media item identifier.</param>
/// <param name="MediaType">The media item type (e.g. Movie, Series, BoxSet).</param>
/// <param name="MediaName">The display name of the media item when blocked.</param>
/// <param name="CreatedAtUtc">The UTC timestamp when the block was created.</param>
public sealed record ContentBlockRecord(
    Guid UserId,
    Guid MediaId,
    string MediaType,
    string MediaName,
    DateTime CreatedAtUtc);
