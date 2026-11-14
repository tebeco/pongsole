using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http.Extensions;
using Pongsole.Contracts;

namespace Pongsole.Server;

internal sealed class DiscordSignIn(bool enabled, Uri? origin)
{
    private const string OAuthScheme = "Discord";
    private const string CookieScheme = "PongsoleDiscord";
    private const string LoginPath = "/discord/login";
    private const string LogoutPath = "/discord/logout";
    private const string FailedRedirect = "/?discord=failed";
    internal const string CallbackPath = "/signin-discord";
    internal const string ApiHost = "discord.com";

    internal static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Pong:Discord");
        var address = settings["Origin"];
        var clientId = settings["ClientId"];
        var clientSecret = settings["ClientSecret"];
        if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(clientId) &&
            string.IsNullOrWhiteSpace(clientSecret))
        {
            services.AddSingleton(new DiscordSignIn(false, null));
            return;
        }

        Uri? origin = null;
        if (!string.IsNullOrWhiteSpace(address) &&
            (!Uri.TryCreate(address, UriKind.Absolute, out origin) || origin.Scheme != Uri.UriSchemeHttps ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.UserInfo + origin.Query + origin.Fragment)))
        {
            throw new InvalidOperationException("Pong:Discord:Origin must be an HTTPS origin.");
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Discord sign-in requires Pong:Discord:ClientId and ClientSecret.");

        services.AddSingleton(new DiscordSignIn(true, origin));
        services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-PongsoleDiscord";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
            })
            .AddOAuth(OAuthScheme, options =>
            {
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.SignInScheme = CookieScheme;
                options.CallbackPath = CallbackPath;
                options.AuthorizationEndpoint = $"https://{ApiHost}/oauth2/authorize";
                options.TokenEndpoint = $"https://{ApiHost}/api/oauth2/token";
                options.UserInformationEndpoint = $"https://{ApiHost}/api/v10/users/@me";
                options.Scope.Add("identify");
                options.UsePkce = true;
                options.BackchannelTimeout = TimeSpan.FromSeconds(10);
                options.BackchannelHttpHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
                options.Events.OnCreatingTicket = ReadPlayerAsync;
                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect(FailedRedirect);
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });

        services.PostConfigure<OAuthOptions>(OAuthScheme, options =>
            options.Backchannel.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"DiscordBot (https://github.com/tebeco/pongsole, {typeof(DiscordSignIn).Assembly.GetName().Version})"));

        // The provider's failure messages can contain details from the token exchange.
        services.AddLogging(logging => logging.AddFilter(
            "Microsoft.AspNetCore.Authentication.OAuth.OAuthHandler", LogLevel.None));
    }

    internal void UseCallbackGuard(WebApplication app)
    {
        if (!enabled)
            return;

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == CallbackPath)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (!IsAvailable(context.Request) || !HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next(context);
        });
    }

    internal void MapEndpoints(WebApplication app, GameAccess game)
    {
        if (!enabled)
            return;

        app.MapPost(LoginPath, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsAvailable(context.Request) || !BrowserAccess.HasSameOrigin(context.Request) ||
                !(game.IsLoopback ? GameAccess.IsLocalBrowser(context) : await BrowserAccess.HasTicketAsync(context)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OAuthScheme]);
        });

        app.MapPost(LogoutPath, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsAvailable(context.Request) || !BrowserAccess.HasSameOrigin(context.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            await context.SignOutAsync(CookieScheme);
            return Results.Redirect("/");
        });
    }

    internal bool IsAvailable(HttpRequest request) =>
        enabled && request.IsHttps && !request.PathBase.HasValue && (origin is null ||
            Uri.TryCreate(UriHelper.BuildAbsolute(request.Scheme, request.Host), UriKind.Absolute, out var current) &&
            current.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) && current.Port == origin.Port);

    internal async Task<DiscordPlayer?> GetPlayerAsync(HttpContext context)
    {
        if (!IsAvailable(context.Request))
            return null;

        var ticket = await context.AuthenticateAsync(CookieScheme);
        return ticket.Succeeded && ticket.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is { } id &&
            ticket.Principal.FindFirstValue(ClaimTypes.Name) is { } username
                ? new DiscordPlayer(id, username)
                : null;
    }

    private static async Task ReadPlayerAsync(OAuthCreatingTicketContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        var cancellationToken = context.HttpContext.RequestAborted;
        using var response = await context.Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var user = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var id = user.RootElement.GetProperty("id").GetString();
        var username = user.RootElement.GetProperty("username").GetString();
        if (id is not { Length: >= 1 and <= 20 } || !id.All(char.IsAsciiDigit) ||
            !PlayerName.IsValidDisplayName(username))
        {
            throw new AuthenticationFailureException("Discord returned an invalid user profile.");
        }

        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
        identity.AddClaim(new Claim(ClaimTypes.Name, username));
    }
}

internal sealed record DiscordPlayer(string Id, string Username);
