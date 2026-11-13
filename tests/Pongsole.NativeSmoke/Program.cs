using Pongsole.Transport;
using Pongsole.Transport.Native;

if (args.Length != 1)
    throw new ArgumentException("Provide the published application's directory.");

var output = Path.GetFullPath(args[0]);
if (!Path.GetFullPath(AppContext.BaseDirectory).Equals(
        Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new InvalidOperationException("Run the smoke program from the published application directory.");

await using var peer = new NativeRtcPeerFactory().Create([
    new RtcIceServer(["turn:turn.cloudflare.com:3478?transport=udp"], "fixture-user", "fixture-password")
]);
var control = peer.CreateChannel("control", reliable: true);
var realtime = peer.CreateChannel("realtime", reliable: false);
if (control.Label != "control" || realtime.Label != "realtime")
    throw new InvalidOperationException("Unexpected native data channels.");
Console.WriteLine("Published native WebRTC library loaded and created both channels.");
