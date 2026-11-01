using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentHider.Data;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentHider.Tasks;

/// <summary>
/// Native Jellyfin scheduled task (<see cref="IScheduledTask"/>) that prunes orphaned content blocks
/// for deleted users or deleted media items and verifies per-user Magic Playlists.
/// </summary>
public sealed partial class ContentHiderCleanupTask : IScheduledTask
{
    private readonly IContentBlockRepository _repository;
    private readonly IContentBlockService _blockService;
    private readonly IMagicPlaylistService _magicPlaylistService;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ContentHiderCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHiderCleanupTask"/> class.
    /// </summary>
    /// <param name="repository">The content block repository.</param>
    /// <param name="blockService">The content block service.</param>
    /// <param name="magicPlaylistService">The magic playlist service.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The logger.</param>
    public ContentHiderCleanupTask(
        IContentBlockRepository repository,
        IContentBlockService blockService,
        IMagicPlaylistService magicPlaylistService,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<ContentHiderCleanupTask> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(magicPlaylistService);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _blockService = blockService;
        _magicPlaylistService = magicPlaylistService;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Content Hider Cleanup & Sync";

    /// <inheritdoc />
    public string Key => "ContentHiderCleanupTask";

    /// <inheritdoc />
    public string Description => "Prunes orphaned content blocks for deleted users or media items in content_blocks.json and synchronizes per-user Magic Playlists.";

    /// <inheritdoc />
    public string Category => "Content Hider";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);

        var existingUserIds = _userManager.GetUsers().Select(u => u.Id).ToHashSet();
        var allSettings = _repository.GetAllUserSettings();
        var allBlocks = _repository.GetAllBlocks();

        var orphanedUserIds = allSettings.Select(s => s.UserId)
            .Concat(allBlocks.Select(b => b.UserId))
            .Distinct()
            .Where(uid => !existingUserIds.Contains(uid))
            .ToList();

        var purgedUsers = 0;
        foreach (var orphanUserId in orphanedUserIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _repository.DeleteAllUserData(orphanUserId);
            purgedUsers++;
        }

        progress.Report(40);

        var purgedMediaCount = 0;
        if (!_libraryManager.IsScanRunning)
        {
            var distinctMediaIds = allBlocks.Select(b => b.MediaId).Distinct().ToList();
            foreach (var mediaId in distinctMediaIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_libraryManager.GetItemById(mediaId) is null)
                {
                    purgedMediaCount += _repository.DeleteMediaBlocks(mediaId);
                }
            }
        }

        progress.Report(75);

        _blockService.ReloadFromDisk();
        _ = _blockService.ProcessExpiredTimers();
        await _magicPlaylistService.EnsureAllUsersMagicPlaylistsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var userId in existingUserIds)
        {
            _magicPlaylistService.SyncFavoriteIndicator(userId);
        }

        LogCleanupCompleted(_logger, purgedUsers, purgedMediaCount);
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
        Message = "Content Hider cleanup completed: removed {PurgedUsers} orphaned user(s) and {PurgedMediaCount} orphaned media block(s).")]
    private static partial void LogCleanupCompleted(ILogger logger, int purgedUsers, int purgedMediaCount);
}
