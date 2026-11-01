using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.ContentHider.Helpers;

/// <summary>
/// Helper methods for resolving the authenticated Jellyfin user from an HTTP request or action context.
/// </summary>
public static class RequestUserHelper
{
    /// <summary>
    /// The claim type used by Jellyfin for storing the authenticated user's GUID.
    /// </summary>
    public const string JellyfinUserIdClaimType = "Jellyfin-UserId";

    /// <summary>
    /// Extracts the authenticated user ID from a <see cref="ClaimsPrincipal"/>.
    /// </summary>
    /// <param name="principal">The claims principal.</param>
    /// <returns>The user's GUID, or <see cref="Guid.Empty"/> if not present.</returns>
    public static Guid GetClaimUserId(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return Guid.Empty;
        }

        var claim = principal.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, JellyfinUserIdClaimType, StringComparison.OrdinalIgnoreCase));

        return claim is not null && Guid.TryParse(claim.Value, out var userId)
            ? userId
            : Guid.Empty;
    }

    /// <summary>
    /// Resolves the effective Jellyfin user ID from the HTTP context and optional action arguments.
    /// Prefers the authenticated claim user ID; falls back to route/query <c>userId</c> when present.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="actionArguments">Optional MVC action arguments.</param>
    /// <returns>The resolved user GUID, or <see cref="Guid.Empty"/>.</returns>
    public static Guid ResolveUserId(HttpContext? httpContext, IDictionary<string, object?>? actionArguments = null)
    {
        var claimUserId = GetClaimUserId(httpContext?.User);
        if (claimUserId != Guid.Empty)
        {
            return claimUserId;
        }

        if (actionArguments is not null && actionArguments.TryGetValue("userId", out var argVal))
        {
            if (argVal is Guid guidVal && guidVal != Guid.Empty)
            {
                return guidVal;
            }

            if (argVal is string strVal && Guid.TryParse(strVal, out var parsedArg) && parsedArg != Guid.Empty)
            {
                return parsedArg;
            }
        }

        if (httpContext?.Request.Query.TryGetValue("userId", out var queryVals) == true)
        {
            var first = queryVals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first) && Guid.TryParse(first, out var queryGuid) && queryGuid != Guid.Empty)
            {
                return queryGuid;
            }
        }

        return Guid.Empty;
    }
}
