using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentHider.Helpers;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.ContentHider.Filters;

/// <summary>
/// Global ASP.NET Core action filter that suppresses user-blocked media across all Jellyfin API endpoints
/// via pre-query <c>excludeItemIds</c> injection and post-query DTO filtering.
/// </summary>
public sealed class ContentHiderActionFilter : IAsyncActionFilter
{
    private readonly IContentBlockService _blockService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHiderActionFilter"/> class.
    /// </summary>
    /// <param name="blockService">The content block service.</param>
    public ContentHiderActionFilter(IContentBlockService blockService)
    {
        ArgumentNullException.ThrowIfNull(blockService);
        _blockService = blockService;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var userId = RequestUserHelper.ResolveUserId(context.HttpContext, context.ActionArguments);
        if (userId == Guid.Empty || !_blockService.IsBlockingActive(userId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (IsTargetingMagicPlaylist(userId, context.ActionArguments))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var blockedIds = _blockService.GetExpandedBlockedIds(userId);
        if (blockedIds.Count == 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Layer 1: Pre-action excludeItemIds injection for accurate pagination & TotalRecordCount.
        InjectExcludeItemIds(context.ActionArguments, blockedIds);

        var executedContext = await next().ConfigureAwait(false);

        // Layer 2: Post-action DTO filtering for endpoints without excludeItemIds and nested items.
        if (executedContext.Result is ObjectResult { Value: not null } objectResult)
        {
            FilterObjectResultValue(userId, objectResult, blockedIds);
        }
    }

    private static void InjectExcludeItemIds(IDictionary<string, object?> actionArguments, IReadOnlySet<Guid> blockedIds)
    {
        if (!actionArguments.TryGetValue("excludeItemIds", out var existingVal))
        {
            return;
        }

        var merged = new HashSet<Guid>(blockedIds);
        if (existingVal is IEnumerable<Guid> existingGuids)
        {
            foreach (var id in existingGuids)
            {
                if (id != Guid.Empty)
                {
                    merged.Add(id);
                }
            }
        }

        actionArguments["excludeItemIds"] = merged.ToArray();
    }

    private bool IsTargetingMagicPlaylist(Guid userId, IDictionary<string, object?> actionArguments)
    {
        var magicPlaylistId = _blockService.GetMagicPlaylistId(userId);
        if (!magicPlaylistId.HasValue || magicPlaylistId.Value == Guid.Empty)
        {
            return false;
        }

        if (TryExtractGuid(actionArguments, "playlistId", out var playlistId) && playlistId == magicPlaylistId.Value)
        {
            return true;
        }

        if (TryExtractGuid(actionArguments, "parentId", out var parentId) && parentId == magicPlaylistId.Value)
        {
            return true;
        }

        if (TryExtractGuid(actionArguments, "itemId", out var itemId) && itemId == magicPlaylistId.Value)
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractGuid(IDictionary<string, object?> args, string key, out Guid value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            if (raw is Guid g)
            {
                value = g;
                return true;
            }

            if (raw is string s && Guid.TryParse(s, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = Guid.Empty;
        return false;
    }

    private void FilterObjectResultValue(Guid userId, ObjectResult objectResult, IReadOnlySet<Guid> blockedIds)
    {
        switch (objectResult.Value)
        {
            case QueryResult<BaseItemDto> queryResult when queryResult.Items is not null:
            {
                var originalItems = queryResult.Items;
                var filtered = new List<BaseItemDto>(originalItems.Count);
                foreach (var dto in originalItems)
                {
                    if (dto is not null && !_blockService.IsDtoBlocked(userId, dto, blockedIds))
                    {
                        filtered.Add(dto);
                    }
                }

                if (filtered.Count != originalItems.Count)
                {
                    var removedCount = originalItems.Count - filtered.Count;
                    queryResult.Items = filtered;
                    queryResult.TotalRecordCount = Math.Max(filtered.Count, queryResult.TotalRecordCount - removedCount);
                }

                break;
            }

            case IEnumerable<BaseItemDto> dtoSequence:
            {
                var filtered = dtoSequence
                    .Where(dto => dto is not null && !_blockService.IsDtoBlocked(userId, dto, blockedIds))
                    .ToArray();
                objectResult.Value = filtered;
                break;
            }

            case IEnumerable<RecommendationDto> recommendations:
            {
                var filteredRecs = new List<RecommendationDto>();
                foreach (var rec in recommendations)
                {
                    if (rec is null)
                    {
                        continue;
                    }

                    if (rec.CategoryId != Guid.Empty && blockedIds.Contains(rec.CategoryId))
                    {
                        continue;
                    }

                    var remainingItems = (rec.Items ?? [])
                        .Where(dto => dto is not null && !_blockService.IsDtoBlocked(userId, dto, blockedIds))
                        .ToArray();

                    if (remainingItems.Length > 0)
                    {
                        rec.Items = remainingItems;
                        filteredRecs.Add(rec);
                    }
                }

                objectResult.Value = filteredRecs;
                break;
            }

            case SearchHintResult searchHintResult when searchHintResult.SearchHints is not null:
            {
                var originalHints = searchHintResult.SearchHints;
                var filteredHints = originalHints
                    .Where(hint => hint is not null && !_blockService.IsSearchHintBlocked(userId, hint, blockedIds))
                    .ToArray();

                if (filteredHints.Length != originalHints.Count)
                {
                    var removedCount = originalHints.Count - filteredHints.Length;
                    var newTotal = Math.Max(filteredHints.Length, searchHintResult.TotalRecordCount - removedCount);
                    objectResult.Value = new SearchHintResult(filteredHints, newTotal);
                }

                break;
            }
        }
    }
}
