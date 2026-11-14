using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Pongsole.Server;

internal sealed class BrowserAccess
{
    private const string Scheme = "PongsoleBrowser";
    private const string InstanceClaim = "pongsole:instance";
    private readonly string instance = Guid.NewGuid().ToString("N");

    internal static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<BrowserAccess>();
        services.AddAuthentication().AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "__Host-PongsoleAccess";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<TimeProvider, BrowserAccess>(
            (options, clock, access) =>
            {
                options.TimeProvider = clock;
                options.Events.OnValidatePrincipal = access.ValidatePrincipalAsync;
            });
    }

    internal void MapEndpoints(WebApplication app, GameAccess game)
    {
        app.MapGet("/browser/session", async (HttpContext context, DiscordSignIn discord) =>
        {
            var request = context.Request;
            context.Response.Headers.CacheControl = "no-store";
            if ((request.Headers.ContainsKey("Origin") && !HasSameOrigin(request)) ||
                game.IsLoopback && !GameAccess.IsLocalBrowser(context) ||
                !game.IsLoopback && !request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var authorized = game.IsLoopback || await HasTicketAsync(context);
            var discordAvailable = authorized && discord.IsAvailable(request);
            var player = discordAvailable ? await discord.GetPlayerAsync(context) : null;
            return Results.Json(new
            {
                mode = game.IsLoopback ? "loopback" : "private",
                authorized,
                discordAvailable,
                discordUsername = player?.Username
            });
        });

        app.MapPost("/browser/session", async (HttpContext context) =>
        {
            var request = context.Request;
            context.Response.Headers.CacheControl = "no-store";
            if (!HasSameOrigin(request) ||
                !request.Headers.TryGetValue("X-Pong-Browser", out var marker) || marker.Count != 1 || marker[0] != "1")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (game.IsLoopback)
                return GameAccess.IsLocalBrowser(context) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);

            var rejection = game.CodeRejection(request);
            if (rejection is int status)
                return Results.StatusCode(status);

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InstanceClaim, instance)], Scheme));
            await context.SignInAsync(Scheme, principal);
            return Results.NoContent();
        });
    }

    internal static async Task<bool> HasTicketAsync(HttpContext context) =>
        (await context.AuthenticateAsync(Scheme)).Succeeded;

    internal static bool HasSameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin) || origin.Count != 1 || origin[0] is not { } value)
            return false;

        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0 || value.AsSpan(separator + 3).ContainsAny('/', '?', '#') ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '@' or ','))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var supplied) ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host.ToUriComponent()}", UriKind.Absolute, out var effective))
        {
            return false;
        }

        return supplied.Scheme == effective.Scheme && supplied.Port == effective.Port &&
            supplied.IdnHost.Equals(effective.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    private Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirstValue(InstanceClaim) != instance ||
            context.Properties.ExpiresUtc is not { } expiration || expiration <= context.Options.TimeProvider!.GetUtcNow())
        {
            context.RejectPrincipal();
        }

        return Task.CompletedTask;
    }
}
