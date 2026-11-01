using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentHider.Tasks;

/// <summary>
/// Native Jellyfin scheduled task (<see cref="IScheduledTask"/>) that prunes orphaned media links
/// from per-user Magic Playlists and ensures every user has their <c>__is_content_hider_playlist</c> playlist provisioned.
/// </summary>
public sealed partial class ContentHiderCleanupTask : IScheduledTask
{
    private readonly IContentBlockService _blockService;
    private readonly IMagicPlaylistService _magicPlaylistService;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ContentHiderCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHiderCleanupTask"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="magicPlaylistService">The magic playlist service.</param>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The logger.</param>
    public ContentHiderCleanupTask(
        IContentBlockService blockService,
        IMagicPlaylistService magicPlaylistService,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<ContentHiderCleanupTask> logger)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(magicPlaylistService);
        ArgumentNullException.ThrowIfNull(playlistManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);

        _blockService = blockService;
        _magicPlaylistService = magicPlaylistService;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Content Hider Cleanup & Sync";

    /// <inheritdoc />
    public string Key => "ContentHiderCleanupTask";

    /// <inheritdoc />
    public string Description => "Prunes orphaned media items from per-user Magic Playlists and synchronizes playlist tags and Favorite indicators.";

    /// <inheritdoc />
    public string Category => "Content Hider";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);

        var users = _userManager.GetUsers().ToList();
        var purgedMediaCount = 0;

        if (!_libraryManager.IsScanRunning)
        {
            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var playlistId = _blockService.GetMagicPlaylistId(user.Id);
                if (!playlistId.HasValue || playlistId.Value == Guid.Empty)
                {
                    continue;
                }

                var playlist = _playlistManager.GetPlaylistForUser(playlistId.Value, user.Id)
                               ?? _libraryManager.GetItemById(playlistId.Value) as Playlist;
                if (playlist?.LinkedChildren is null)
                {
                    continue;
                }

                var candidateIds = playlist.LinkedChildren
                    .Select(lc => lc.ItemId ?? Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                foreach (var mediaId in candidateIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = _libraryManager.GetItemById(mediaId);
                    if (!ContentBlockService.IsHideableItem(item))
                    {
                        if (await _blockService.RemoveBlockAsync(user.Id, mediaId, cancellationToken).ConfigureAwait(false))
                        {
                            purgedMediaCount++;
                        }
                    }
                }
            }
        }

        progress.Report(60);

        _ = _blockService.ProcessExpiredTimers();
        await _magicPlaylistService.EnsureAllUsersMagicPlaylistsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var user in users)
        {
            _magicPlaylistService.SyncFavoriteIndicator(user.Id);
        }

        LogCleanupCompleted(_logger, purgedMediaCount);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Content Hider cleanup completed: removed {PurgedMediaCount} orphaned or invalid media block(s).")]
    private static partial void LogCleanupCompleted(ILogger logger, int purgedMediaCount);
}
