using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.Extensions;

namespace Pongsole.Server;

internal static class Homepage
{
    internal static void UseHomepage(this WebApplication app)
    {
        using var file = app.Environment.WebRootFileProvider.GetFileInfo("index.html").CreateReadStream();
        using var reader = new StreamReader(file);
        var template = reader.ReadToEnd();
        var headEnd = template.IndexOf("</head>", StringComparison.Ordinal);
        if (headEnd < 0)
            throw new InvalidOperationException("The homepage template requires a closing head tag.");

        var before = template[..headEnd];
        var after = template[headEnd..];
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) ||
                !(request.Path.Equals("/") || request.Path.Equals("/index.html")))
            {
                await next(context);
                return;
            }

            var root = AbsoluteUrl(request, "/");
            var image = AbsoluteUrl(request, "/images/pongsole-preview.gif");
            var metadata = $"""
                  <meta property="og:title" content="Pongsole">
                  <meta property="og:type" content="website">
                  <meta property="og:site_name" content="Pongsole">
                  <meta property="og:description" content="Join a team, share a paddle, and play multiplayer Pong with multiball pickups.">
                  <meta property="og:url" content="{root}">
                  <meta property="og:image" content="{image}">
                  <meta property="og:image:type" content="image/gif">
                  <meta property="og:image:width" content="1864">
                  <meta property="og:image:height" content="726">
                  <meta property="og:image:alt" content="Pongsole multiplayer Pong gameplay">
                  <meta name="twitter:card" content="summary_large_image">
                  <meta name="theme-color" content="#53edf3">
                """;
            var html = before + metadata + "\n" + after;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            if (!HttpMethods.IsHead(request.Method))
                await context.Response.WriteAsync(html, context.RequestAborted);
        });
    }

    private static string AbsoluteUrl(HttpRequest request, PathString path) =>
        HtmlEncoder.Default.Encode(UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, path));
}
