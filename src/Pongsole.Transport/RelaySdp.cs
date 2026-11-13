using System.Globalization;
using System.Net;

namespace Pongsole.Transport;

public static class RelaySdp
{
    private const int MaxCharacters = 64 * 1024;

    public static string Sanitize(string sdp) => Transform(sdp, outgoing: true);

    public static bool IsValid(string sdp)
    {
        try
        {
            Transform(sdp, outgoing: false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string Transform(string sdp, bool outgoing)
    {
        Check(sdp.Length is > 0 and <= MaxCharacters);
        var lines = sdp.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var result = new List<string>(lines.Length);
        var origin = 0;
        var media = 0;
        var candidates = 0;

        foreach (var line in lines)
        {
            Check(line.Length >= 2 && line[1] == '=' && !line.Any(char.IsControl));
            var output = line;
            if (line.StartsWith("o=", StringComparison.Ordinal))
            {
                Check(++origin == 1);
                output = Address(line, 6, outgoing);
            }
            else if (line.StartsWith("c=", StringComparison.Ordinal))
            {
                output = Address(line, 3, outgoing);
            }
            else if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                var fields = Fields(line);
                Check(++media == 1 && fields is ["m=application", _, "UDP/DTLS/SCTP", "webrtc-datachannel"]);
            }
            else if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Candidate(line, outgoing);
                if (candidate is null)
                    continue;

                Check(++candidates <= 16);
                output = candidate;
            }
            else
            {
                Check(!line.StartsWith("a=remote-candidates:", StringComparison.OrdinalIgnoreCase));
            }

            result.Add(output);
        }

        Check(origin == 1 && media == 1 && candidates > 0);
        return string.Join("\r\n", result) + "\r\n";
    }

    private static string Address(string line, int count, bool outgoing)
    {
        var fields = Fields(line);
        Check(fields.Length == count);
        var network = fields[^3];
        Check((count == 3 ? network == "c=IN" : network == "IN") && fields[^2] is "IP4" or "IP6");
        var placeholder = fields[^2] == "IP4" ? "0.0.0.0" : "::";
        Check(outgoing || fields[^1] == placeholder);
        fields[^1] = placeholder;
        return string.Join(' ', fields);
    }

    private static string? Candidate(string line, bool outgoing)
    {
        var fields = Fields(line);
        var relay = fields.Length >= 8 && fields[2].Equals("udp", StringComparison.OrdinalIgnoreCase) &&
            fields[6] == "typ" && fields[7] == "relay";
        if (!relay && outgoing)
            return null;

        Check(relay && fields.Length % 2 == 0 && fields[1] == "1" &&
            uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            IPAddress.TryParse(fields[4], out var address) && IsPublic(address) &&
            ushort.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0);

        for (var i = 8; i < fields.Length; i += 2)
        {
            switch (fields[i])
            {
                case "raddr":
                    Check(outgoing || fields[i + 1] is "0.0.0.0" or "::");
                    fields[i + 1] = fields[4].Contains(':') ? "::" : "0.0.0.0";
                    break;
                case "rport":
                    Check(outgoing || fields[i + 1] == "0");
                    fields[i + 1] = "0";
                    break;
                case "generation":
                case "network-id":
                case "network-cost":
                    Check(uint.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _));
                    break;
                default:
                    throw InvalidSdp();
            }
        }

        return string.Join(' ', fields);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] is > 0 and < 224 and not 10 and not 127 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168)
            : (bytes[0] & 0xe0) == 0x20;
    }

    private static string[] Fields(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void Check(bool valid)
    {
        if (!valid)
            throw InvalidSdp();
    }

    private static InvalidDataException InvalidSdp() => new("Invalid relay-only SDP.");
}
