using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Pongsole.Tunnel;

internal sealed class CloudflaredInstaller(HttpClient http)
{
    private const long MaximumBytes = 200 * 1024 * 1024;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(120);

    internal Task<InstalledCloudflared> PrepareAsync(CancellationToken cancellation)
    {
        var release = CloudflaredRelease.Current();
        var cacheRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(cacheRoot))
            throw new IOException("No per-user application data directory is available.");

        return PrepareAsync(release, Path.Combine(cacheRoot, "Pongsole", "cloudflared",
            CloudflaredRelease.Version, release.Runtime), cancellation);
    }

    internal async Task<InstalledCloudflared> PrepareAsync(CloudflaredRelease release, string cache,
        CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(InstallTimeout);
        var token = deadline.Token;
        Directory.CreateDirectory(cache);
        using var cacheLock = await LockAsync(Path.Combine(cache, ".install.lock"), token);

        var asset = Path.Combine(cache, release.Asset);
        if (!File.Exists(asset))
        {
            var pending = Path.Combine(cache, $"{Guid.NewGuid():N}.download");
            try
            {
                using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumBytes)
                    throw new InvalidDataException("The cloudflared download exceeds the size limit.");

                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = File.Create(pending))
                    await CopyBoundedAsync(input, output, token);

                await VerifyAsync(pending, release, token);
                File.Move(pending, asset);
            }
            finally
            {
                File.Delete(pending);
            }
        }

        // Stage each execution from the same verified file handle. No unpacked binary is trusted on reuse.
        var directory = Directory.CreateTempSubdirectory("pongsole-cloudflared-");
        var executable = Path.Combine(directory.FullName, release.Executable);
        try
        {
            await using var input = File.OpenRead(asset);
            await VerifyAsync(input, release, token);
            input.Position = 0;
            if (release.IsArchive)
                await ExtractAsync(input, executable, token);
            else
            {
                await using var output = File.Create(executable);
                await CopyBoundedAsync(input, output, token);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new InstalledCloudflared(directory.FullName, executable);
        }
        catch
        {
            directory.Delete(recursive: true);
            throw;
        }
    }

    internal static async Task ExtractAsync(Stream archive, string target, CancellationToken cancellation)
    {
        using var decompressed = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(decompressed);
        var entry = await tar.GetNextEntryAsync(cancellationToken: cancellation);
        if (entry is null || entry.Name is not ("cloudflared" or "./cloudflared") ||
            entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
            entry.Length is <= 0 or > MaximumBytes || entry.DataStream is not { } data)
        {
            throw new InvalidDataException("The cloudflared archive has an unexpected entry.");
        }

        await using (var output = File.Create(target))
            await CopyBoundedAsync(data, output, cancellation);
        if (await tar.GetNextEntryAsync(cancellationToken: cancellation) is not null)
            throw new InvalidDataException("The cloudflared archive has unexpected additional entries.");
    }

    private static async Task VerifyAsync(string path, CloudflaredRelease release, CancellationToken cancellation)
    {
        await using var file = File.OpenRead(path);
        await VerifyAsync(file, release, cancellation);
    }

    private static async Task VerifyAsync(FileStream file, CloudflaredRelease release, CancellationToken cancellation)
    {
        if (file.Length is <= 0 or > MaximumBytes ||
            !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancellation),
                Convert.FromHexString(release.Sha256)))
        {
            throw new InvalidDataException("Cloudflared checksum verification failed. Remove the cached release asset before retrying.");
        }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation);
            }
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
            {
                total += read;
                if (total > MaximumBytes)
                    throw new InvalidDataException("The cloudflared asset exceeds the size limit.");

                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class InstalledCloudflared(string directory, string executable) : IDisposable
{
    internal string DirectoryPath { get; } = directory;
    internal string ExecutablePath { get; } = executable;

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
