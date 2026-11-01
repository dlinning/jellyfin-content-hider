using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.ContentHider.Filters;

/// <summary>
/// Intercepts native client actions on a user's Magic Playlist (tagged with <c>__is_content_hider_playlist</c>)
/// so Web, Mobile, and TV apps can add show/movie blocks, view/remove blocked items, and Favorite-toggle blocking on/off.
/// </summary>
public sealed class MagicPlaylistActionFilter : IAsyncActionFilter
{
    /// <summary>
    /// Message returned and displayed to the user when attempting to hide a single episode or unsupported item type.
    /// </summary>
    public const string OnlyShowsAndMoviesMessage = "Only shows and movies can be hidden.";

    private readonly IContentBlockService _blockService;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly ISessionManager? _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MagicPlaylistActionFilter"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="dtoService">The Jellyfin DTO service.</param>
    /// <param name="sessionManager">Optional Jellyfin session manager for sending user notifications.</param>
    public MagicPlaylistActionFilter(
        IContentBlockService blockService,
        IUserManager userManager,
        IDtoService dtoService,
        ISessionManager? sessionManager = null)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(dtoService);

        _blockService = blockService;
        _userManager = userManager;
        _dtoService = dtoService;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var userId = RequestUserHelper.ResolveUserId(context.HttpContext, context.ActionArguments);
        if (userId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var magicPlaylistId = _blockService.GetMagicPlaylistId(userId);
        if (!magicPlaylistId.HasValue || magicPlaylistId.Value == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var request = context.HttpContext.Request;
        var path = request.Path.Value ?? string.Empty;

        // 1. Check if this is a Playlist operation targeting the user's Magic Playlist.
        if (TryExtractGuid(context.ActionArguments, "playlistId", out var playlistId) &&
            playlistId == magicPlaylistId.Value &&
            path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPost(request.Method))
            {
                await HandleAddItemsToMagicPlaylistAsync(userId, context).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsGet(request.Method))
            {
                HandleGetMagicPlaylistItems(userId, context);
                return;
            }

            if (HttpMethods.IsDelete(request.Method))
            {
                await HandleRemoveItemsFromMagicPlaylistAsync(userId, context).ConfigureAwait(false);
                return;
            }
        }

        // Also handle standard GET /Items?ParentId=<magicPlaylistId> used by some TV clients when opening a playlist.
        if (HttpMethods.IsGet(request.Method) &&
            TryExtractGuid(context.ActionArguments, "parentId", out var parentId) &&
            parentId == magicPlaylistId.Value)
        {
            HandleGetMagicPlaylistItems(userId, context);
            return;
        }

        // 2. Check if the user is Favoriting / Unfavoriting their Magic Playlist to pause/resume blocking.
        if (TryExtractGuid(context.ActionArguments, "itemId", out var favoriteItemId) &&
            favoriteItemId == magicPlaylistId.Value &&
            path.Contains("FavoriteItems", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);

            if (HttpMethods.IsPost(request.Method))
            {
                // Favoriting the Magic Playlist pauses blocking (using DefaultPauseDurationMinutes).
                _blockService.SetBlockingEnabled(userId, false);
            }
            else if (HttpMethods.IsDelete(request.Method))
            {
                // Unfavoriting the Magic Playlist immediately re-enables blocking.
                _blockService.SetBlockingEnabled(userId, true);
            }

            return;
        }

        await next().ConfigureAwait(false);
    }

    private async Task HandleAddItemsToMagicPlaylistAsync(Guid userId, ActionExecutingContext context)
    {
        var ids = ExtractGuidList(context, "ids");
        var anyAdded = false;
        var anyRejected = false;

        foreach (var id in ids)
        {
            if (id == Guid.Empty)
            {
                continue;
            }

            var added = await _blockService
                .TryAddBlockAsync(userId, id, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (added)
            {
                anyAdded = true;
            }
            else
            {
                anyRejected = true;
            }
        }

        if (anyRejected)
        {
            await TryNotifyUserAsync(userId, OnlyShowsAndMoviesMessage, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!anyAdded)
            {
                context.Result = new BadRequestObjectResult(OnlyShowsAndMoviesMessage);
                return;
            }
        }

        context.Result = new NoContentResult();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort session toast notification should not fail the HTTP response.")]
    private async Task TryNotifyUserAsync(Guid userId, string text, CancellationToken cancellationToken)
    {
        if (_sessionManager?.Sessions is null)
        {
            return;
        }

        var message = new MessageCommand
        {
            Header = "Content Hider",
            Text = text,
            TimeoutMs = 5000
        };

        foreach (var session in _sessionManager.Sessions)
        {
            if (session is not null && session.UserId == userId && !string.IsNullOrEmpty(session.Id))
            {
                try
                {
                    await _sessionManager
                        .SendMessageCommand(session.Id, session.Id, message, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ignore disconnected client session errors.
                }
            }
        }
    }

    private void HandleGetMagicPlaylistItems(Guid userId, ActionExecutingContext context)
    {
        var user = _userManager.GetUserById(userId);
        var items = _blockService.GetBlockedItems(userId);

        var totalCount = items.Count;
        int? startIndex = context.ActionArguments.TryGetValue("startIndex", out var sVal) && sVal is int sInt ? sInt : null;
        int? limit = context.ActionArguments.TryGetValue("limit", out var lVal) && lVal is int lInt ? lInt : null;

        IEnumerable<BaseItem> pagedItems = items;
        if (startIndex.HasValue && startIndex.Value > 0)
        {
            pagedItems = pagedItems.Skip(startIndex.Value);
        }

        if (limit.HasValue && limit.Value > 0)
        {
            pagedItems = pagedItems.Take(limit.Value);
        }

        var dtoOptions = new DtoOptions();
        if (context.ActionArguments.TryGetValue("fields", out var fieldsVal) && fieldsVal is ItemFields[] fields)
        {
            dtoOptions.Fields = fields;
        }

        if (context.ActionArguments.TryGetValue("enableImages", out var enableImgVal) && enableImgVal is bool enableImages)
        {
            dtoOptions.EnableImages = enableImages;
        }

        if (context.ActionArguments.TryGetValue("imageTypeLimit", out var imgLimitVal) && imgLimitVal is int imageTypeLimit)
        {
            dtoOptions.ImageTypeLimit = imageTypeLimit;
        }

        if (context.ActionArguments.TryGetValue("enableImageTypes", out var imgTypesVal) && imgTypesVal is ImageType[] enableImageTypes)
        {
            dtoOptions.ImageTypes = enableImageTypes;
        }

        var dtos = _dtoService.GetBaseItemDtos(pagedItems.ToList(), dtoOptions, user);
        foreach (var dto in dtos)
        {
            dto.PlaylistItemId = dto.Id.ToString("N", CultureInfo.InvariantCulture);
        }

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(startIndex, totalCount, dtos));
    }

    private async Task HandleRemoveItemsFromMagicPlaylistAsync(Guid userId, ActionExecutingContext context)
    {
        var idsToRemove = new HashSet<Guid>();
        foreach (var id in ExtractGuidList(context, "entryIds"))
        {
            idsToRemove.Add(id);
        }

        foreach (var id in ExtractGuidList(context, "ids"))
        {
            idsToRemove.Add(id);
        }

        foreach (var mediaId in idsToRemove)
        {
            if (mediaId != Guid.Empty)
            {
                _ = await _blockService
                    .RemoveBlockAsync(userId, mediaId, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }

        context.Result = new NoContentResult();
    }

    private static List<Guid> ExtractGuidList(ActionExecutingContext context, string key)
    {
        var list = new List<Guid>();
        if (context.ActionArguments.TryGetValue(key, out var raw))
        {
            if (raw is IEnumerable<Guid> guids)
            {
                list.AddRange(guids.Where(g => g != Guid.Empty));
                return list;
            }

            if (raw is IEnumerable<string> strings)
            {
                foreach (var str in strings)
                {
                    if (Guid.TryParse(str, out var parsed) && parsed != Guid.Empty)
                    {
                        list.Add(parsed);
                    }
                }

                return list;
            }
        }

        if (context.HttpContext.Request.Query.TryGetValue(key, out var queryVals))
        {
            foreach (var val in queryVals)
            {
                if (string.IsNullOrWhiteSpace(val))
                {
                    continue;
                }

                foreach (var token in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(token, out var parsed) && parsed != Guid.Empty)
                    {
                        list.Add(parsed);
                    }
                }
            }
        }

        return list;
    }

    private static bool TryExtractGuid(IDictionary<string, object?> args, string key, out Guid value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            if (raw is Guid g && g != Guid.Empty)
            {
                value = g;
                return true;
            }

            if (raw is string s && Guid.TryParse(s, out var parsed) && parsed != Guid.Empty)
            {
                value = parsed;
                return true;
            }
        }

        value = Guid.Empty;
        return false;
    }
}
