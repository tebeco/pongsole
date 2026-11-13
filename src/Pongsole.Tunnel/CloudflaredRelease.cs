using System.Runtime.InteropServices;

namespace Pongsole.Tunnel;

internal sealed record CloudflaredRelease(string Runtime, string Asset, string Sha256)
{
    internal const string Version = "2026.10.7";

    internal Uri DownloadUrl => new($"https://github.com/cloudflare/cloudflared/releases/download/{Version}/{Asset}");
    internal bool IsArchive => Asset.EndsWith(".tgz", StringComparison.Ordinal);
    internal string Executable => Runtime.StartsWith("win-", StringComparison.Ordinal) ? "cloudflared.exe" : "cloudflared";

    // Use release asset digests; the macOS archives differ from the hashes in the release notes.
    // https://github.com/cloudflare/cloudflared/releases/tag/2026.10.7
    internal static CloudflaredRelease Select(OSPlatform platform, Architecture architecture) => (platform, architecture) switch
    {
        (var os, Architecture.X64) when os == OSPlatform.Windows =>
            new("win-x64", "cloudflared-windows-amd64.exe", "2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343"),
        (var os, Architecture.X64) when os == OSPlatform.Linux =>
            new("linux-x64", "cloudflared-linux-amd64", "1f99fb518b83dec2ba8d870a7cc991d82a77e6196295a3d2754ba17ab9f91872"),
        (var os, Architecture.Arm64) when os == OSPlatform.Linux =>
            new("linux-arm64", "cloudflared-linux-arm64", "3df43aedc9ff291d1735b5cb576cb97e0b157488796db2e32b28aecabb445775"),
        (var os, Architecture.X64) when os == OSPlatform.OSX =>
            new("osx-x64", "cloudflared-darwin-amd64.tgz", "02f3edfd87684dd5b705607688e4eb4b323f6e777b81e7b3a85af556aa356848"),
        (var os, Architecture.Arm64) when os == OSPlatform.OSX =>
            new("osx-arm64", "cloudflared-darwin-arm64.tgz", "3586ccf397e98e5f437e60e7c307ca6ac463e110580503af8bb8e6f9ebf831bd"),
        _ => throw new PlatformNotSupportedException("No pinned cloudflared asset is available for this platform.")
    };

    internal static CloudflaredRelease Current() => Select(
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsLinux() ? OSPlatform.Linux :
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("unsupported"),
        RuntimeInformation.ProcessArchitecture);
}
