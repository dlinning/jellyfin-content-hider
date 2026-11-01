using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.ContentHider.Services;

/// <summary>
/// Consumes native Jellyfin user lifecycle events (<see cref="UserCreatedEventArgs"/> and <see cref="UserDeletedEventArgs"/>)
/// to provision Magic Playlists for new users and purge SQLite rows when users are deleted.
/// </summary>
public sealed class UserLifecycleEventConsumer :
    IEventConsumer<UserCreatedEventArgs>,
    IEventConsumer<UserDeletedEventArgs>
{
    private readonly IContentBlockService _blockService;
    private readonly IMagicPlaylistService _magicPlaylistService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLifecycleEventConsumer"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="magicPlaylistService">The magic playlist service.</param>
    public UserLifecycleEventConsumer(
        IContentBlockService blockService,
        IMagicPlaylistService magicPlaylistService)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(magicPlaylistService);

        _blockService = blockService;
        _magicPlaylistService = magicPlaylistService;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (eventArgs.Argument is { } user && user.Id != Guid.Empty)
        {
            await _magicPlaylistService.EnsureUserMagicPlaylistAsync(user.Id).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (eventArgs.Argument is { } user && user.Id != Guid.Empty)
        {
            _blockService.PurgeUserData(user.Id);
        }

        return Task.CompletedTask;
    }
}
