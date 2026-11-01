using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentHider.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Provisions per-user <c>[Hide Content]</c> playlists and keeps their Favorite state synchronized with blocking status.
/// </summary>
public sealed partial class MagicPlaylistService : IMagicPlaylistService
{
    private readonly IContentBlockService _blockService;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<MagicPlaylistService> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MagicPlaylistService"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userDataManager">The Jellyfin user data manager.</param>
    /// <param name="logger">The logger.</param>
    public MagicPlaylistService(
        IContentBlockService blockService,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<MagicPlaylistService> logger)
        : this(
            blockService,
            playlistManager,
            userManager,
            libraryManager,
            userDataManager,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MagicPlaylistService"/> class with a custom configuration provider.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userDataManager">The Jellyfin user data manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configProvider">The configuration provider delegate.</param>
    public MagicPlaylistService(
        IContentBlockService blockService,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<MagicPlaylistService> logger,
        Func<PluginConfiguration> configProvider)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(playlistManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configProvider);

        _blockService = blockService;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <inheritdoc />
    public async Task<Guid?> EnsureUserMagicPlaylistAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var config = _configProvider();
        if (!config.EnableMagicPlaylist || userId == Guid.Empty)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var desiredName = string.IsNullOrWhiteSpace(config.MagicPlaylistName)
            ? "[Hide Content]"
            : config.MagicPlaylistName.Trim();

        var existingId = _blockService.GetMagicPlaylistId(userId);
        if (existingId.HasValue && existingId.Value != Guid.Empty)
        {
            var existingPlaylist = _playlistManager.GetPlaylistForUser(existingId.Value, userId);
            if (existingPlaylist is not null)
            {
                SyncFavoriteIndicator(userId);
                return existingId.Value;
            }
        }

        // Check if the user already has a playlist matching the configured MagicPlaylistName.
        var matchByName = _playlistManager
            .GetPlaylists(userId)
            .FirstOrDefault(p =>
                p.OwnerUserId == userId &&
                string.Equals(p.Name, desiredName, StringComparison.OrdinalIgnoreCase));

        if (matchByName is not null)
        {
            _blockService.SetMagicPlaylistId(userId, matchByName.Id);
            SyncFavoriteIndicator(userId);
            return matchByName.Id;
        }

        var creationResult = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = desiredName,
            UserId = userId,
            Public = false,
            ItemIdList = []
        }).ConfigureAwait(false);

        if (creationResult is not null && Guid.TryParse(creationResult.Id, out var createdPlaylistId) && createdPlaylistId != Guid.Empty)
        {
            _blockService.SetMagicPlaylistId(userId, createdPlaylistId);
            SyncFavoriteIndicator(userId);
            LogProvisionedMagicPlaylist(_logger, desiredName, createdPlaylistId, userId);
            return createdPlaylistId;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task EnsureAllUsersMagicPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        if (!_configProvider().EnableMagicPlaylist)
        {
            return;
        }

        foreach (var user in _userManager.GetUsers())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await EnsureUserMagicPlaylistAsync(user.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMagicPlaylistProvisionError(_logger, user.Id, ex);
            }
        }
    }

    /// <inheritdoc />
    public void SyncFavoriteIndicator(Guid userId)
    {
        var playlistId = _blockService.GetMagicPlaylistId(userId);
        if (!playlistId.HasValue || playlistId.Value == Guid.Empty)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        var playlistItem = _libraryManager.GetItemById(playlistId.Value);
        if (user is null || playlistItem is null)
        {
            return;
        }

        var shouldBeFavorite = !_blockService.IsBlockingActive(userId);
        var userData = _userDataManager.GetUserData(user, playlistItem);
        if (userData is not null && userData.IsFavorite != shouldBeFavorite)
        {
            userData.IsFavorite = shouldBeFavorite;
            _userDataManager.SaveUserData(user, playlistItem, userData, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provisioned Magic Playlist '{PlaylistName}' ({PlaylistId}) for user {UserId}.")]
    private static partial void LogProvisionedMagicPlaylist(
        ILogger logger,
        string playlistName,
        Guid playlistId,
        Guid userId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to provision Magic Playlist for user {UserId}.")]
    private static partial void LogMagicPlaylistProvisionError(
        ILogger logger,
        Guid userId,
        Exception ex);
}
