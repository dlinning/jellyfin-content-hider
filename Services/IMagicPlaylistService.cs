using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Manages per-user Magic Playlists (tagged with <c>__is_content_hider_playlist</c>) and synchronizes their Favorite badge with blocking state.
/// </summary>
public interface IMagicPlaylistService
{
    /// <summary>
    /// Ensures a private Magic Playlist tagged with <c>__is_content_hider_playlist</c> exists for the specified user.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Magic Playlist GUID, or <c>null</c> if user not found.</returns>
    Task<Guid?> EnsureUserMagicPlaylistAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures Magic Playlists exist for all existing Jellyfin users.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task EnsureAllUsersMagicPlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes the Favorite (heart) indicator on the user's Magic Playlist so that
    /// <c>IsFavorite == true</c> when blocking is paused and <c>IsFavorite == false</c> when blocking is active.
    /// </summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    void SyncFavoriteIndicator(Guid userId);
}
