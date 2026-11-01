using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Background hosted service that initializes on-disk storage, provisions per-user Magic Playlists,
/// and periodically checks for expired pause timers to restore blocking and clear Favorite indicators.
/// </summary>
public sealed partial class MagicPlaylistHostedService : IHostedService, IDisposable
{
    private readonly IContentBlockService _blockService;
    private readonly IMagicPlaylistService _magicPlaylistService;
    private readonly ILogger<MagicPlaylistHostedService> _logger;
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MagicPlaylistHostedService"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="magicPlaylistService">The magic playlist service.</param>
    /// <param name="logger">The logger.</param>
    public MagicPlaylistHostedService(
        IContentBlockService blockService,
        IMagicPlaylistService magicPlaylistService,
        ILogger<MagicPlaylistHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(magicPlaylistService);
        ArgumentNullException.ThrowIfNull(logger);

        _blockService = blockService;
        _magicPlaylistService = magicPlaylistService;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Hosted service startup must not crash the Jellyfin server.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _blockService.ReloadFromDisk();
            await _magicPlaylistService.EnsureAllUsersMagicPlaylistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStartupWarning(_logger, ex);
        }

        _timer = new Timer(OnTimerTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background timer callback must not throw unhandled exceptions.")]
    private void OnTimerTick(object? state)
    {
        try
        {
            var expiredUserIds = _blockService.ProcessExpiredTimers();
            foreach (var userId in expiredUserIds)
            {
                _magicPlaylistService.SyncFavoriteIndicator(userId);
                LogPauseTimerExpired(_logger, userId);
            }
        }
        catch (Exception ex)
        {
            LogTimerError(_logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Non-fatal error while initializing Content Hider startup tasks.")]
    private static partial void LogStartupWarning(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Content blocking pause timer expired for user {UserId}; blocking automatically re-enabled.")]
    private static partial void LogPauseTimerExpired(ILogger logger, Guid userId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Error during Content Hider timer check.")]
    private static partial void LogTimerError(ILogger logger, Exception ex);
}
