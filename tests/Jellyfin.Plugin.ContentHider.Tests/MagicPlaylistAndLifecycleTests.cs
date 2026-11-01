using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Data;
using Jellyfin.Plugin.ContentHider.Filters;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using Jellyfin.Plugin.ContentHider.Tasks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class MagicPlaylistAndLifecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;
    private readonly JsonContentBlockRepository _repository;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IDtoService> _dtoServiceMock;
    private readonly Mock<IPlaylistManager> _playlistManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly ContentBlockService _blockService;
    private readonly MagicPlaylistService _magicPlaylistService;
    private readonly MagicPlaylistActionFilter _magicFilter;

    public MagicPlaylistAndLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentHiderMagicTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storagePath = Path.Combine(_tempDir, "users");
        _repository = new JsonContentBlockRepository(_storagePath);
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _dtoServiceMock = new Mock<IDtoService>();
        _playlistManagerMock = new Mock<IPlaylistManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();

        _blockService = new ContentBlockService(
            _repository,
            _libraryManagerMock.Object,
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
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _dtoServiceMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MagicPlaylistWorkflow_AddListRemoveAndFavoriteToggle_WorksAcrossNativeClients()
    {
        var userId = Guid.NewGuid();
        var magicPlaylistId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        _blockService.SetMagicPlaylistId(userId, magicPlaylistId);

        var movieEntity = new Movie { Id = movieId, Name = "Interstellar" };
        _libraryManagerMock.Setup(m => m.GetItemById(movieId)).Returns(movieEntity);
        _dtoServiceMock
            .Setup(d => d.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>(), It.IsAny<bool>()))
            .Returns([new BaseItemDto { Id = movieId, Name = "Interstellar" }]);

        // 1. TV client calls POST /Playlists/{magicPlaylistId}/Items?ids={movieId}
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
        Assert.Single(_repository.GetUserBlocks(userId));

        // 2. TV client opens [Hide Content] playlist -> GET /Playlists/{magicPlaylistId}/Items
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

        // 3. TV client Favorites [Hide Content] -> POST /UserFavoriteItems/{magicPlaylistId} -> pauses blocking!
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

        // 4. TV client Unfavorites [Hide Content] -> DELETE /UserFavoriteItems/{magicPlaylistId} -> resumes blocking!
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

        // 5. TV client removes item from [Hide Content] -> DELETE /Playlists/{magicPlaylistId}/Items?entryIds={movieId:N}
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
        Assert.Empty(_repository.GetUserBlocks(userId));
    }

    [Fact]
    public async Task NativeEventConsumerAndScheduledTask_ProvisionAndPruneOrphanedData()
    {
        var activeUser = new User("doug", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = Guid.NewGuid()
        };
        var deletedUser = new User("olduser", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = Guid.NewGuid()
        };

        var provisionedPlaylistId = Guid.NewGuid();
        _userManagerMock.Setup(u => u.GetUserById(activeUser.Id)).Returns(activeUser);
        _userManagerMock.Setup(u => u.GetUsers()).Returns([activeUser]);
        _playlistManagerMock.Setup(p => p.GetPlaylists(activeUser.Id)).Returns([]);
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(provisionedPlaylistId.ToString("D")));

        var consumer = new UserLifecycleEventConsumer(_blockService, _magicPlaylistService);

        // 1. UserCreatedEventArgs provisions Magic Playlist.
        await consumer.OnEvent(new UserCreatedEventArgs(activeUser));
        Assert.Equal(provisionedPlaylistId, _blockService.GetMagicPlaylistId(activeUser.Id));

        // 2. UserDeletedEventArgs purges user blocks & settings from storage.
        _blockService.AddBlock(deletedUser.Id, Guid.NewGuid(), exactItemOnly: true);
        Assert.Single(_repository.GetUserBlocks(deletedUser.Id));
        await consumer.OnEvent(new UserDeletedEventArgs(deletedUser));
        Assert.Empty(_repository.GetUserBlocks(deletedUser.Id));

        // 3. ContentHiderCleanupTask (IScheduledTask) prunes deleted media items while keeping existing media.
        var existingMediaId = Guid.NewGuid();
        var deletedMediaId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.IsScanRunning).Returns(false);
        _libraryManagerMock.Setup(l => l.GetItemById(existingMediaId)).Returns(new Movie { Id = existingMediaId, Name = "Existing" });
        _libraryManagerMock.Setup(l => l.GetItemById(deletedMediaId)).Returns((BaseItem?)null);

        _blockService.AddBlock(activeUser.Id, existingMediaId, exactItemOnly: true);
        _blockService.AddBlock(activeUser.Id, deletedMediaId, exactItemOnly: true);
        Assert.Equal(2, _repository.GetUserBlocks(activeUser.Id).Count);

        var cleanupTask = new ContentHiderCleanupTask(
            _repository,
            _blockService,
            _magicPlaylistService,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            NullLogger<ContentHiderCleanupTask>.Instance);

        await cleanupTask.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var remaining = _repository.GetUserBlocks(activeUser.Id);
        Assert.Single(remaining);
        Assert.Equal(existingMediaId, remaining[0].MediaId);
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
