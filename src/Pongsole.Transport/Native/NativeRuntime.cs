using System.Runtime.InteropServices;

namespace Pongsole.Transport.Native;

internal static class NativeRuntime
{
    private static readonly Lazy<bool> Loaded = new(Load);
    private static nint library;

    internal static void EnsureLoaded() => _ = Loaded.Value;

    private static bool Load()
    {
        var (rid, file) = (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(),
            RuntimeInformation.ProcessArchitecture) switch
        {
            (true, _, _, Architecture.X64) => ("win-x64", "datachannel.dll"),
            (_, true, _, Architecture.X64) => ("linux-x64", "libdatachannel.so"),
            (_, true, _, Architecture.Arm64) => ("linux-arm64", "libdatachannel.so"),
            (_, _, true, Architecture.X64) => ("osx-x64", "libdatachannel.dylib"),
            (_, _, true, Architecture.Arm64) => ("osx-arm64", "libdatachannel.dylib"),
            _ => throw new PlatformNotSupportedException("Native WebRTC requires a supported operating system and architecture.")
        };

        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
        if (!NativeLibrary.TryLoad(path, out library))
            throw new IOException("The packaged native WebRTC library could not be loaded. Check the platform runtime and its dependencies.");

        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly,
            (name, _, _) => name == "datachannel" ? library : 0);
        NativeMethods.rtcInitLogger(0, 0);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NativeMethods.rtcCleanup();
        return true;
    }
}
