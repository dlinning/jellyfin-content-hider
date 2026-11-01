using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentHider.Configuration;
using Jellyfin.Plugin.ContentHider.Data;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContentHider.Api;

/// <summary>
/// Serves the self-service Content Hider user web portal, client script, and user-scoped REST API endpoints.
/// </summary>
[ApiController]
[Route("ContentHider")]
public sealed class ContentHiderController : ControllerBase
{
    private readonly IContentBlockRepository _repository;
    private readonly IContentBlockService _blockService;
    private readonly IMagicPlaylistService _magicPlaylistService;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHiderController"/> class.
    /// </summary>
    /// <param name="repository">The content block repository.</param>
    /// <param name="blockService">The content block service.</param>
    /// <param name="magicPlaylistService">The magic playlist service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    public ContentHiderController(
        IContentBlockRepository repository,
        IContentBlockService blockService,
        IMagicPlaylistService magicPlaylistService,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(magicPlaylistService);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);

        _repository = repository;
        _blockService = blockService;
        _magicPlaylistService = magicPlaylistService;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Serves the mobile-friendly self-service Content Hider user web portal HTML.
    /// </summary>
    /// <returns>The portal HTML page.</returns>
    [HttpGet("")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPortalPage()
    {
        return ServeEmbeddedResource("Web.portal.html", MediaTypeNames.Text.Html);
    }

    /// <summary>
    /// Serves the Jellyfin Web client enhancement script.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        return ServeEmbeddedResource("Web.clientScript.js", "application/javascript");
    }

    /// <summary>
    /// Gets the authenticated user's current content blocking status and settings.
    /// </summary>
    /// <returns>The user's blocking status.</returns>
    [HttpGet("Api/Status")]
    [Authorize]
    [ProducesResponseType(typeof(UserBlockingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<UserBlockingStatusDto> GetStatus()
    {
        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(BuildStatusDto(userId));
    }

    /// <summary>
    /// Toggles content blocking on or off (manually or with a countdown timer) for the authenticated user.
    /// </summary>
    /// <param name="request">The toggle request payload.</param>
    /// <returns>The updated user blocking status.</returns>
    [HttpPost("Api/Toggle")]
    [Authorize]
    [ProducesResponseType(typeof(UserBlockingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<UserBlockingStatusDto> ToggleBlocking([FromBody] ToggleBlockingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        _ = _blockService.SetBlockingEnabled(userId, request.Enabled, request.DurationMinutes);
        _magicPlaylistService.SyncFavoriteIndicator(userId);
        return Ok(BuildStatusDto(userId));
    }

    /// <summary>
    /// Lists all media items currently blocked by the authenticated user.
    /// </summary>
    /// <returns>The list of blocked items.</returns>
    [HttpGet("Api/Blocks")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<ContentHiderItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<ContentHiderItemDto>> GetBlocks()
    {
        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var records = _blockService.GetUserBlocks(userId);
        var result = new List<ContentHiderItemDto>(records.Count);
        foreach (var record in records)
        {
            var item = _libraryManager.GetItemById(record.MediaId);
            var name = !string.IsNullOrWhiteSpace(item?.Name)
                ? item.Name
                : (!string.IsNullOrWhiteSpace(record.MediaName) ? record.MediaName : record.MediaId.ToString("D", CultureInfo.InvariantCulture));
            var type = item is not null
                ? item.GetBaseItemKind().ToString()
                : (!string.IsNullOrWhiteSpace(record.MediaType) ? record.MediaType : "Item");

            result.Add(new ContentHiderItemDto(
                record.MediaId,
                name,
                type,
                item?.ProductionYear,
                item?.HasImage(ImageType.Primary) == true,
                true,
                record.CreatedAtUtc));
        }

        return Ok(result);
    }

    /// <summary>
    /// Adds a content block for the authenticated user and persists it to on-disk storage.
    /// </summary>
    /// <param name="mediaId">The media item identifier to hide.</param>
    /// <param name="exact">If <c>true</c>, blocks the exact item without normalizing episodes/seasons to their parent series.</param>
    /// <returns>The created block DTO.</returns>
    [HttpPost("Api/Blocks/{mediaId}")]
    [Authorize]
    [ProducesResponseType(typeof(ContentHiderItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<ContentHiderItemDto> AddBlock([FromRoute] Guid mediaId, [FromQuery] bool exact = false)
    {
        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty || mediaId == Guid.Empty)
        {
            return Unauthorized();
        }

        var record = _blockService.AddBlock(userId, mediaId, exact);
        var item = _libraryManager.GetItemById(record.MediaId);
        return Ok(new ContentHiderItemDto(
            record.MediaId,
            item?.Name ?? record.MediaName,
            item?.GetBaseItemKind().ToString() ?? record.MediaType,
            item?.ProductionYear,
            item?.HasImage(ImageType.Primary) == true,
            true,
            record.CreatedAtUtc));
    }

    /// <summary>
    /// Removes a content block for the authenticated user from on-disk storage.
    /// </summary>
    /// <param name="mediaId">The media item identifier to unhide.</param>
    /// <returns>204 No Content.</returns>
    [HttpDelete("Api/Blocks/{mediaId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult RemoveBlock([FromRoute] Guid mediaId)
    {
        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        _ = _blockService.RemoveBlock(userId, mediaId);
        return NoContent();
    }

    /// <summary>
    /// Searches the library for Movies, Series, and BoxSets (including currently hidden ones)
    /// so the user can hide or unhide items from the portal.
    /// </summary>
    /// <param name="query">Optional search term.</param>
    /// <param name="limit">Maximum number of items to return (default 40).</param>
    /// <returns>List of matching items annotated with block status.</returns>
    [HttpGet("Api/Search")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<ContentHiderItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<ContentHiderItemDto>> SearchLibrary(
        [FromQuery] string? query = null,
        [FromQuery] int limit = 40)
    {
        var userId = RequestUserHelper.ResolveUserId(HttpContext);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId);
        var cappedLimit = Math.Clamp(limit, 1, 100);
        var itemsQuery = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet],
            SearchTerm = string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Limit = cappedLimit
        };

        var items = _libraryManager.GetItemList(itemsQuery) ?? [];
        var directBlocked = _blockService.GetUserBlocks(userId).ToDictionary(b => b.MediaId);

        var dtos = items
            .Where(i => i is not null && i.Id != Guid.Empty)
            .Select(i =>
            {
                var isBlocked = directBlocked.TryGetValue(i.Id, out var rec);
                return new ContentHiderItemDto(
                    i.Id,
                    i.Name ?? string.Empty,
                    i.GetBaseItemKind().ToString(),
                    i.ProductionYear,
                    i.HasImage(ImageType.Primary),
                    isBlocked,
                    rec?.CreatedAtUtc);
            })
            .ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Returns on-disk storage status for the Admin Dashboard configuration page.
    /// </summary>
    /// <returns>Storage info DTO.</returns>
    [HttpGet("Api/AdminInfo")]
    [Authorize]
    [ProducesResponseType(typeof(AdminDatabaseInfoDto), StatusCodes.Status200OK)]
    public ActionResult<AdminDatabaseInfoDto> GetAdminInfo()
    {
        _repository.EnsureInitialized();
        var dbPath = _repository.DatabaseFilePath;
        var dirInfo = new DirectoryInfo(dbPath);
        var totalSize = dirInfo.Exists
            ? dirInfo.EnumerateFiles("*.json", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0L;
        var allBlocks = _repository.GetAllBlocks();

        return Ok(new AdminDatabaseInfoDto(
            dbPath,
            dirInfo.Exists,
            totalSize,
            allBlocks.Count));
    }

    private UserBlockingStatusDto BuildStatusDto(Guid userId)
    {
        var isEffectivelyBlocking = _blockService.IsBlockingActive(userId);
        var settings = _blockService.GetUserSettings(userId);
        var blocks = _blockService.GetUserBlocks(userId);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return new UserBlockingStatusDto(
            userId,
            settings.IsBlockingEnabled,
            isEffectivelyBlocking,
            settings.DisabledUntilUtc,
            config.DefaultPauseDurationMinutes,
            blocks.Count,
            settings.MagicPlaylistId);
    }

    private ActionResult ServeEmbeddedResource(string relativeResourceName, string contentType)
    {
        var assembly = typeof(Plugin).Assembly;
        var fullResourceName = string.Format(CultureInfo.InvariantCulture, "{0}.{1}", typeof(Plugin).Namespace, relativeResourceName);
        var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, contentType);
    }
}
