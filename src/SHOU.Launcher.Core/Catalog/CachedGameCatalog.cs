using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Catalog;

/// <summary>Only public version identities are cached. Installation still resolves and verifies official metadata.</summary>
public sealed class CachedGameCatalog(string cacheDirectory, Func<DateTimeOffset>? clock = null)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public async Task<GameCatalog> GetAsync(string sourceConfiguration, bool forceRefresh,
        Func<CancellationToken, Task<GameCatalog>> fetch, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceConfiguration)));
            var path = Path.Combine(Path.GetFullPath(cacheDirectory), "catalog", "minecraft-" + key + ".json");
            var cached = await ReadAsync(path, ct).ConfigureAwait(false);
            if (!forceRefresh && cached?.FetchedAt is { } fetched && Now - fetched < TimeSpan.FromMinutes(15))
                return cached with { FromCache = true };
            GameCatalog result;
            try
            {
                result = (await fetch(ct).ConfigureAwait(false)) with { FetchedAt = Now, FromCache = false, Notice = null };
                ct.ThrowIfCancellationRequested(); Validate(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or AggregateException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                if (cached is null) throw;
                return cached with { FromCache = true, Notice = "暂时无法更新，正在显示上次成功读取的目录。可检查网络后刷新。" };
            }
            try { await WriteAsync(path, result, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result = result with { Notice = "目录已读取，但本地缓存未保存。" }; }
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally { gate.Release(); }
    }
    private async Task<GameCatalog?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            VerifiedDownloader.RejectLink(path);
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, true);
            if (stream.Length is <= 0 or > MaximumBytes) return null;
            // A bounded copy also handles a file growing while it is being read.
            using var bytes = new MemoryStream(); var buffer = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > MaximumBytes) return null;
                bytes.Write(buffer, 0, count);
            }
            var catalog = JsonSerializer.Deserialize<GameCatalog>(bytes.ToArray());
            if (catalog is null) return null;
            Validate(catalog);
            return catalog with { FromCache = false, Notice = null };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    private void Validate(GameCatalog catalog)
    {
        if (catalog.FetchedAt is not { } date || date > Now.AddMinutes(5) || date == DateTimeOffset.MinValue
            || string.IsNullOrEmpty(catalog.Source) || catalog.Source.Length > 253 || Uri.CheckHostName(catalog.Source) == UriHostNameType.Unknown
            || catalog.Versions is null || catalog.Versions.Count is 0 or > 20000)
            throw new InvalidDataException("版本目录缓存无效。");
        foreach (var entry in catalog.Versions)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Id) || entry.Id.Length > 160 || entry.Id.Any(char.IsControl)
                || string.IsNullOrEmpty(entry.Kind) || entry.Kind.Length > 40 || entry.Kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
                throw new InvalidDataException("版本目录缓存条目无效。");
            if (entry.CanInstall)
                try { _ = SafePath.Identifier(entry.Id); }
                catch (ArgumentException) { throw new InvalidDataException("版本目录缓存安装标识无效。"); }
        }
    }
    private static async Task WriteAsync(string path, GameCatalog catalog, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        if (bytes.Length > MaximumBytes) throw new IOException("版本目录缓存超过大小限制。");
        VerifiedDownloader.RejectLink(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VerifiedDownloader.RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true))
            { await output.WriteAsync(bytes, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(path); VerifiedDownloader.RejectLink(temporary);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
