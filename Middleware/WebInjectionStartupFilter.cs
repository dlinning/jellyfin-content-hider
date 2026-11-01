using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.ContentHider.Middleware;

/// <summary>
/// Registers the in-memory <c>/web/index.html</c> script injection middleware without modifying any files on disk.
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (Plugin.Instance?.Configuration.EnableWebUiInjection != true ||
            !HttpMethods.IsGet(context.Request.Method) ||
            !IsWebIndexRequest(context.Request.Path.Value))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Prevent downstream compression of this single small index.html document so we can safely inject our script tag.
        context.Request.Headers.Remove("Accept-Encoding");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context).ConfigureAwait(false);

            buffer.Position = 0;
            var contentType = context.Response.ContentType ?? string.Empty;
            if (context.Response.StatusCode == StatusCodes.Status200OK &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var html = await reader.ReadToEndAsync().ConfigureAwait(false);

                const string ScriptMarker = "/ContentHider/ClientScript.js";
                if (!html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase) &&
                    html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                {
                    var scriptTag = "<script src=\"../ContentHider/ClientScript.js\" defer></script></body>";
                    var modifiedHtml = html.Replace("</body>", scriptTag, StringComparison.OrdinalIgnoreCase);
                    var modifiedBytes = Encoding.UTF8.GetBytes(modifiedHtml);

                    context.Response.ContentLength = modifiedBytes.Length;
                    context.Response.Body = originalBody;
                    await originalBody.WriteAsync(modifiedBytes, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }

            buffer.Position = 0;
            context.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool IsWebIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
