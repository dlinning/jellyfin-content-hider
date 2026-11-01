using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Data;
using Jellyfin.Plugin.ContentHider.Filters;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ContentHider.Tests;

public sealed class ContentHiderActionFilterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;
    private readonly JsonContentBlockRepository _repository;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly ContentBlockService _blockService;
    private readonly ContentHiderActionFilter _filter;

    public ContentHiderActionFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentHiderFilterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storagePath = Path.Combine(_tempDir, "users");
        _repository = new JsonContentBlockRepository(_storagePath);
        _libraryManagerMock = new Mock<ILibraryManager>();
        _blockService = new ContentBlockService(
            _repository,
            _libraryManagerMock.Object,
            () => new PluginConfiguration(),
            () => DateTime.UtcNow);
        _filter = new ContentHiderActionFilter(_blockService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task OnActionExecutionAsync_InjectsExcludeItemIds_AndFiltersQueryResult_ForBlockedUserOnly()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var blockedMovieId = Guid.NewGuid();
        var visibleMovieId = Guid.NewGuid();
        var blockedSeriesId = Guid.NewGuid();
        var childEpisodeId = Guid.NewGuid();

        _blockService.AddBlock(userA, blockedMovieId, exactItemOnly: true);
        _blockService.AddBlock(userA, blockedSeriesId, exactItemOnly: true);

        var args = new Dictionary<string, object?>
        {
            ["excludeItemIds"] = Array.Empty<Guid>()
        };

        var itemsResponse = new QueryResult<BaseItemDto>(
            0,
            3,
            [
                new BaseItemDto { Id = blockedMovieId, Name = "Blocked Movie", Type = BaseItemKind.Movie },
                new BaseItemDto { Id = visibleMovieId, Name = "Visible Movie", Type = BaseItemKind.Movie },
                new BaseItemDto { Id = childEpisodeId, SeriesId = blockedSeriesId, Name = "Blocked Episode", Type = BaseItemKind.Episode }
            ]);

        var ctxA = CreateExecutingContext(userA, "/Items", args);
        await _filter.OnActionExecutionAsync(ctxA, () => Task.FromResult(CreateExecutedContext(ctxA, new ObjectResult(itemsResponse))));

        // Layer 1: excludeItemIds was populated with userA's blocked IDs.
        var injectedExclude = Assert.IsType<Guid[]>(args["excludeItemIds"]);
        Assert.Contains(blockedMovieId, injectedExclude);
        Assert.Contains(blockedSeriesId, injectedExclude);

        // Layer 2: QueryResult was filtered and TotalRecordCount decremented from 3 -> 1.
        Assert.Single(itemsResponse.Items);
        Assert.Equal(visibleMovieId, itemsResponse.Items[0].Id);
        Assert.Equal(1, itemsResponse.TotalRecordCount);

        // User B has no blocks -> receives all 3 items untouched.
        var itemsForUserB = new QueryResult<BaseItemDto>(
            0,
            3,
            [
                new BaseItemDto { Id = blockedMovieId, Name = "Blocked Movie", Type = BaseItemKind.Movie },
                new BaseItemDto { Id = visibleMovieId, Name = "Visible Movie", Type = BaseItemKind.Movie },
                new BaseItemDto { Id = childEpisodeId, SeriesId = blockedSeriesId, Name = "Blocked Episode", Type = BaseItemKind.Episode }
            ]);
        var ctxB = CreateExecutingContext(userB, "/Items", new Dictionary<string, object?>());
        await _filter.OnActionExecutionAsync(ctxB, () => Task.FromResult(CreateExecutedContext(ctxB, new ObjectResult(itemsForUserB))));
        Assert.Equal(3, itemsForUserB.Items.Count);
    }

    [Fact]
    public async Task OnActionExecutionAsync_FiltersLatestMedia_Recommendations_AndSearchHints()
    {
        var userId = Guid.NewGuid();
        var blockedId = Guid.NewGuid();
        var visibleId = Guid.NewGuid();
        _blockService.AddBlock(userId, blockedId, exactItemOnly: true);

        // 1. IEnumerable<BaseItemDto> (GetLatestMedia)
        IEnumerable<BaseItemDto> latest =
        [
            new BaseItemDto { Id = blockedId, Name = "Blocked" },
            new BaseItemDto { Id = visibleId, Name = "Visible" }
        ];
        var latestResult = new ObjectResult(latest);
        var ctxLatest = CreateExecutingContext(userId, "/Items/Latest", new Dictionary<string, object?>());
        await _filter.OnActionExecutionAsync(ctxLatest, () => Task.FromResult(CreateExecutedContext(ctxLatest, latestResult)));
        var filteredLatest = Assert.IsAssignableFrom<IEnumerable<BaseItemDto>>(latestResult.Value).ToList();
        Assert.Single(filteredLatest);
        Assert.Equal(visibleId, filteredLatest[0].Id);

        // 2. SearchHintResult (GetSearchHints)
        var searchResult = new ObjectResult(new SearchHintResult(
            [
                new SearchHint { Id = blockedId, Name = "Blocked" },
                new SearchHint { Id = visibleId, Name = "Visible" }
            ],
            2));
        var ctxSearch = CreateExecutingContext(userId, "/Search/Hints", new Dictionary<string, object?>());
        await _filter.OnActionExecutionAsync(ctxSearch, () => Task.FromResult(CreateExecutedContext(ctxSearch, searchResult)));
        var filteredSearch = Assert.IsType<SearchHintResult>(searchResult.Value);
        Assert.Single(filteredSearch.SearchHints);
        Assert.Equal(visibleId, filteredSearch.SearchHints[0].Id);
        Assert.Equal(1, filteredSearch.TotalRecordCount);

        // 3. When user toggles blocking OFF, blocked items are NOT suppressed.
        _blockService.SetBlockingEnabled(userId, false, 60);
        var unblockedQuery = new QueryResult<BaseItemDto>(
            0,
            2,
            [
                new BaseItemDto { Id = blockedId, Name = "Blocked" },
                new BaseItemDto { Id = visibleId, Name = "Visible" }
            ]);
        var ctxPaused = CreateExecutingContext(userId, "/Items", new Dictionary<string, object?>());
        await _filter.OnActionExecutionAsync(ctxPaused, () => Task.FromResult(CreateExecutedContext(ctxPaused, new ObjectResult(unblockedQuery))));
        Assert.Equal(2, unblockedQuery.Items.Count);
    }

    private static ActionExecutingContext CreateExecutingContext(
        Guid userId,
        string path,
        IDictionary<string, object?> actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(RequestUserHelper.JellyfinUserIdClaimType, userId.ToString("D"))
        ], "JellyfinAuth"));

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], actionArguments, controller: new object());
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext executingContext, IActionResult result)
    {
        return new ActionExecutedContext(executingContext, [], executingContext.Controller)
        {
            Result = result
        };
    }
}
