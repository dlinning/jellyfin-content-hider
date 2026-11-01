using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Filters;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using Jellyfin.Plugin.ContentHider.Tasks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class MagicPlaylistAndLifecycleTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IDtoService> _dtoServiceMock;
    private readonly Mock<IPlaylistManager> _playlistManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ContentBlockService _blockService;
    private readonly MagicPlaylistService _magicPlaylistService;
    private readonly MagicPlaylistActionFilter _magicFilter;

    public MagicPlaylistAndLifecycleTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _dtoServiceMock = new Mock<IDtoService>();
        _playlistManagerMock = new Mock<IPlaylistManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _sessionManagerMock = new Mock<ISessionManager>();

        _blockService = new ContentBlockService(
            _playlistManagerMock.Object,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            () => new PluginConfiguration(),
            () => DateTime.UtcNow);

        _magicPlaylistService = new MagicPlaylistService(
            _blockService,
            _playlistManagerMock.Object,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            _userDataManagerMock.Object,
            NullLogger<MagicPlaylistService>.Instance,
            () => new PluginConfiguration());

        _magicFilter = new MagicPlaylistActionFilter(
            _blockService,
            _userManagerMock.Object,
            _dtoServiceMock.Object,
            _sessionManagerMock.Object);
    }

    [Fact]
    public async Task MagicPlaylistWorkflow_AddListRemoveRejectEpisodeAndFavoriteToggle_WorksAcrossNativeClients()
    {
        var userId = Guid.NewGuid();
        var magicPlaylistId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var playlist = new Playlist
        {
            Id = magicPlaylistId,
            OwnerUserId = userId,
            Name = "[doug: Hide Content]",
            Tags = [MagicPlaylistService.ManagedPlaylistTag]
        };

        _playlistManagerMock.Setup(p => p.GetPlaylists(userId)).Returns([playlist]);
        _playlistManagerMock.Setup(p => p.GetPlaylistForUser(magicPlaylistId, userId)).Returns(playlist);
        _blockService.SetMagicPlaylistId(userId, magicPlaylistId);

        var movieEntity = new Movie { Id = movieId, Name = "Interstellar" };
        var episodeEntity = new Episode { Id = episodeId, Name = "Pilot", SeriesId = Guid.NewGuid() };
        _libraryManagerMock.Setup(m => m.GetItemById(movieId)).Returns(movieEntity);
        _libraryManagerMock.Setup(m => m.GetItemById(episodeId)).Returns(episodeEntity);
        _dtoServiceMock
            .Setup(d => d.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>(), It.IsAny<bool>()))
            .Returns([new BaseItemDto { Id = movieId, Name = "Interstellar" }]);

        // 1. Attempting to add a single Episode is rejected with "Only shows and movies can be hidden."
        var addEpisodeCtx = CreateContext(
            userId,
            HttpMethods.Post,
            $"/Playlists/{magicPlaylistId}/Items",
            new Dictionary<string, object?>
            {
                ["playlistId"] = magicPlaylistId,
                ["ids"] = new[] { episodeId }
            });

        await _magicFilter.OnActionExecutionAsync(addEpisodeCtx, () => Task.FromResult(new ActionExecutedContext(addEpisodeCtx, [], new object())));
        var badRequest = Assert.IsType<BadRequestObjectResult>(addEpisodeCtx.Result);
        Assert.Equal(MagicPlaylistActionFilter.OnlyShowsAndMoviesMessage, badRequest.Value);
        Assert.Empty(_blockService.GetBlockedItems(userId));

        // 2. Client calls POST /Playlists/{magicPlaylistId}/Items?ids={movieId}
        var addCtx = CreateContext(
            userId,
            HttpMethods.Post,
            $"/Playlists/{magicPlaylistId}/Items",
            new Dictionary<string, object?>
            {
                ["playlistId"] = magicPlaylistId,
                ["ids"] = new[] { movieId }
            });

        var nextCalled = false;
        await _magicFilter.OnActionExecutionAsync(addCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(addCtx, [], new object()));
        });

        Assert.False(nextCalled);
        Assert.IsType<NoContentResult>(addCtx.Result);
        Assert.Single(_blockService.GetBlockedItems(userId));

        // 3. Client opens [doug: Hide Content] playlist -> GET /Playlists/{magicPlaylistId}/Items
        var getCtx = CreateContext(
            userId,
            HttpMethods.Get,
            $"/Playlists/{magicPlaylistId}/Items",
            new Dictionary<string, object?>
            {
                ["playlistId"] = magicPlaylistId
            });

        await _magicFilter.OnActionExecutionAsync(getCtx, () => Task.FromResult(new ActionExecutedContext(getCtx, [], new object())));
        var okResult = Assert.IsType<OkObjectResult>(getCtx.Result);
        var playlistItems = Assert.IsType<QueryResult<BaseItemDto>>(okResult.Value);
        Assert.Single(playlistItems.Items);
        Assert.Equal(movieId.ToString("N"), playlistItems.Items[0].PlaylistItemId);

        // 4. Client Favorites [doug: Hide Content] -> POST /UserFavoriteItems/{magicPlaylistId} -> pauses blocking!
        var favCtx = CreateContext(
            userId,
            HttpMethods.Post,
            $"/UserFavoriteItems/{magicPlaylistId}",
            new Dictionary<string, object?>
            {
                ["itemId"] = magicPlaylistId
            });

        await _magicFilter.OnActionExecutionAsync(favCtx, () => Task.FromResult(new ActionExecutedContext(favCtx, [], new object())));
        Assert.False(_blockService.IsBlockingActive(userId));

        // 5. Client Unfavorites [doug: Hide Content] -> DELETE /UserFavoriteItems/{magicPlaylistId} -> resumes blocking!
        var unfavCtx = CreateContext(
            userId,
            HttpMethods.Delete,
            $"/UserFavoriteItems/{magicPlaylistId}",
            new Dictionary<string, object?>
            {
                ["itemId"] = magicPlaylistId
            });

        await _magicFilter.OnActionExecutionAsync(unfavCtx, () => Task.FromResult(new ActionExecutedContext(unfavCtx, [], new object())));
        Assert.True(_blockService.IsBlockingActive(userId));

        // 6. Client removes item from [doug: Hide Content] -> DELETE /Playlists/{magicPlaylistId}/Items?entryIds={movieId:N}
        var delCtx = CreateContext(
            userId,
            HttpMethods.Delete,
            $"/Playlists/{magicPlaylistId}/Items",
            new Dictionary<string, object?>
            {
                ["playlistId"] = magicPlaylistId,
                ["entryIds"] = new[] { movieId.ToString("N") }
            });

        await _magicFilter.OnActionExecutionAsync(delCtx, () => Task.FromResult(new ActionExecutedContext(delCtx, [], new object())));
        Assert.IsType<NoContentResult>(delCtx.Result);
        Assert.Empty(_blockService.GetBlockedItems(userId));
    }

    [Fact]
    public async Task NativeEventConsumerAndScheduledTask_ProvisionTaggedPlaylistWithUsername_AndPruneDeletedMedia()
    {
        var activeUser = new User("doug", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = Guid.NewGuid()
        };

        var provisionedPlaylistId = Guid.NewGuid();
        var createdPlaylist = new Playlist
        {
            Id = provisionedPlaylistId,
            OwnerUserId = activeUser.Id,
            Name = "[doug: Hide Content]"
        };

        PlaylistCreationRequest? capturedCreationRequest = null;
        _userManagerMock.Setup(u => u.GetUserById(activeUser.Id)).Returns(activeUser);
        _userManagerMock.Setup(u => u.GetUsers()).Returns([activeUser]);
        _playlistManagerMock.Setup(p => p.GetPlaylists(activeUser.Id)).Returns([]);
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => capturedCreationRequest = req)
            .ReturnsAsync(new PlaylistCreationResult(provisionedPlaylistId.ToString("D")));
        _playlistManagerMock
            .Setup(p => p.GetPlaylistForUser(provisionedPlaylistId, activeUser.Id))
            .Returns(createdPlaylist);

        var consumer = new UserLifecycleEventConsumer(_blockService, _magicPlaylistService);

        // 1. UserCreatedEventArgs provisions "[doug: Hide Content]" and tags it with "__is_content_hider_playlist".
        await consumer.OnEvent(new UserCreatedEventArgs(activeUser));
        Assert.NotNull(capturedCreationRequest);
        Assert.Equal("[doug: Hide Content]", capturedCreationRequest.Name);
        Assert.Equal(provisionedPlaylistId, _blockService.GetMagicPlaylistId(activeUser.Id));
        Assert.Contains(MagicPlaylistService.ManagedPlaylistTag, createdPlaylist.Tags);

        // 2. ContentHiderCleanupTask (IScheduledTask) prunes deleted media items while keeping existing media.
        var existingMediaId = Guid.NewGuid();
        var deletedMediaId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.IsScanRunning).Returns(false);
        _libraryManagerMock.Setup(l => l.GetItemById(existingMediaId)).Returns(new Movie { Id = existingMediaId, Name = "Existing" });
        _libraryManagerMock.Setup(l => l.GetItemById(deletedMediaId)).Returns(new Movie { Id = deletedMediaId, Name = "To Be Deleted" });

        await _blockService.TryAddBlockAsync(activeUser.Id, existingMediaId);
        await _blockService.TryAddBlockAsync(activeUser.Id, deletedMediaId);
        Assert.Equal(2, createdPlaylist.LinkedChildren.Length);

        // Simulate media item deletion from the library.
        _libraryManagerMock.Setup(l => l.GetItemById(deletedMediaId)).Returns((BaseItem?)null);

        var cleanupTask = new ContentHiderCleanupTask(
            _blockService,
            _magicPlaylistService,
            _playlistManagerMock.Object,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            NullLogger<ContentHiderCleanupTask>.Instance);

        await cleanupTask.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Single(createdPlaylist.LinkedChildren);
        Assert.Equal(existingMediaId, createdPlaylist.LinkedChildren[0].ItemId);
    }

    private static ActionExecutingContext CreateContext(
        Guid userId,
        string method,
        string path,
        IDictionary<string, object?> actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(RequestUserHelper.JellyfinUserIdClaimType, userId.ToString("D"))
        ], "JellyfinAuth"));

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], actionArguments, controller: new object());
    }
}
