using System.Text.Json;
using Pongsole.Transport;

namespace Pongsole.Server;

internal static class RtcEndpoints
{
    private const int MaxOfferBytes = 64 * 1024;

    internal static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/rtc/sessions");
        routes.MapPost("", CreateAsync);
        routes.MapPost("/{id}/offer", OfferAsync);
        routes.MapDelete("/{id}", Delete);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, RtcSessions sessions,
        ITurnCredentialProvider credentials, DiscordSignIn discord, IServiceProvider services)
    {
        context.Response.Headers.CacheControl = "no-store";
        var player = context.Request.Headers.ContainsKey("Origin") ? await discord.GetPlayerAsync(context) : null;
        var reservation = sessions.Reserve(context.Connection.RemoteIpAddress, player);
        if (reservation.Error is { } rejection)
            return Error(rejection, StatusCodes.Status429TooManyRequests);

        var connection = reservation.Connection!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connection.Closed);
        var issued = false;
        try
        {
            var client = await credentials.CreateAsync(deadline.Token).WaitAsync(deadline.Token);
            var server = await credentials.CreateAsync(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (client.IceServers.Any(left => server.IceServers.Any(right =>
                    left.Username == right.Username && left.Credential == right.Credential)))
                return Error("PONGSOLE_TURN_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable);
            var factory = services.GetRequiredService<IRtcPeerFactory>();
            if (!connection.Attach(factory.Create([.. server.IceServers.Select(server => new RtcIceServer(
                    server.Urls, server.Username, server.Credential))])))
                return Error("PONGSOLE_TURN_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable);

            issued = true;
            return Results.Json(new RtcSession(connection.Id, connection.SessionToken, connection.Epoch,
                [.. client.IceServers.Select(server => new RtcIceServer(server.Urls, server.Username, server.Credential))],
                client.ExpiresAt));
        }
        catch (TurnCredentialException error)
        {
            return Error(error.Message == "PONGSOLE_TURN_NOT_CONFIGURED" ? error.Message : "PONGSOLE_TURN_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            return Error("PONGSOLE_TURN_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            if (!issued)
                connection.Close();
        }
    }

    private static async Task<IResult> OfferAsync(HttpContext context, RtcSessions sessions, string id)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (Find(context, sessions, id) is not { } connection)
            return Results.NotFound();

        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > MaxOfferBytes)
            return Results.BadRequest();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connection.Closed);
        try
        {
            var buffer = new byte[MaxOfferBytes + 1];
            var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, deadline.Token);
            if (length > MaxOfferBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var offer = JsonSerializer.Deserialize<RtcDescription>(buffer.AsSpan(0, length), JsonSerializerOptions.Web);
            if (offer?.Sdp is not { } sdp || !RelaySdp.IsValid(sdp))
                return Results.BadRequest();

            if (!connection.TryBeginOffer(out var peer))
                return Results.Conflict();

            try
            {
                var answer = await peer.AcceptOfferAsync(sdp, deadline.Token).WaitAsync(deadline.Token);
                return Results.Json(new RtcDescription(RelaySdp.Sanitize(answer)));
            }
            catch (Exception)
            {
                connection.Close();
                return Error("PONGSOLE_TURN_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable);
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        catch (OperationCanceledException)
        {
            connection.Close();
            return Results.NotFound();
        }
        catch (IOException)
        {
            return Results.BadRequest();
        }
    }

    private static IResult Delete(HttpContext context, RtcSessions sessions, string id)
    {
        context.Response.Headers.CacheControl = "no-store";
        Find(context, sessions, id)?.Close();
        return Results.NoContent();
    }

    private static RtcConnection? Find(HttpContext context, RtcSessions sessions, string id) =>
        context.Request.Headers.TryGetValue("X-Pong-Session", out var token) && token.Count == 1
            ? sessions.Find(id, token.ToString())
            : null;

    private static IResult Error(string code, int status) => Results.Json(new { code }, statusCode: status);
}
