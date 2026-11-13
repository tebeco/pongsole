using System.Net;
using System.Text.RegularExpressions;

namespace Pongsole.Client;

internal static partial class ClientError
{
    [GeneratedRegex(@"\bPONGSOLE_[A-Z_]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    public static string? Marker(Exception? error) => MarkerPattern().Matches(error?.Message ?? string.Empty)
        .Select(match => match.Value)
        .FirstOrDefault(marker => marker is
            "PONGSOLE_INVALID_RESUME_TOKEN" or "PONGSOLE_EXPIRED_RESUME_TOKEN"
            or "PONGSOLE_SESSION_ALREADY_CONNECTED" or "PONGSOLE_ALREADY_JOINED"
            or "PONGSOLE_INVALID_NAME" or "PONGSOLE_INVALID_DIRECTION" or "PONGSOLE_INVALID_SEQUENCE"
            or "PONGSOLE_NOT_JOINED" or "PONGSOLE_CAPACITY_REACHED"
            or "PONGSOLE_INPUT_QUEUE_FULL" or "PONGSOLE_SERVER_STOPPING"
            or "PONGSOLE_MODE_FORBIDDEN" or "PONGSOLE_MODE_LOCKED" or "PONGSOLE_INVALID_MODE");

    public static HttpStatusCode? HttpStatus(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return status;
            }
        }

        return null;
    }

    public static string Explain(string? marker) => marker switch
    {
        "PONGSOLE_SESSION_ALREADY_CONNECTED" => "This player session is still connected. Close the other connection and try again.",
        "PONGSOLE_INVALID_RESUME_TOKEN" or "PONGSOLE_EXPIRED_RESUME_TOKEN" => "The player session could not be resumed.",
        "PONGSOLE_ALREADY_JOINED" => "The server says this connection has already joined.",
        "PONGSOLE_INVALID_NAME" => "The server rejected the display name.",
        "PONGSOLE_INVALID_DIRECTION" => "The server rejected the movement direction.",
        "PONGSOLE_INVALID_SEQUENCE" => "The server rejected the movement sequence.",
        "PONGSOLE_NOT_JOINED" => "The server no longer recognizes the player session.",
        _ => "The server rejected the game request."
    };
}
