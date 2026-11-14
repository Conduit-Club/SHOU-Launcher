using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceArtworkService
{
    public async Task<byte[]?> ReadInstanceIconAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var instance = FindInstance(instanceId); var presentation = instance.Presentation ?? new();
        if (presentation.Icon is not ("auto" or "custom")) return null;
        if (presentation.Icon == "custom" && presentation.IconImageSha256 is { } hash)
        {
            VerifiedDownloader.RejectLink(Paths.LocalDataDirectory);
            var path = SafePath.Within(Paths.LocalDataDirectory, "instance-icons/" + hash + ".png");
            var bytes = await WorldContentArchive.ReadFileAsync(Paths.LocalDataDirectory, path, AccountAppearancePng.MaximumPngBytes, cancellationToken);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("实例图标校验失败。");
            _ = AccountAppearancePng.DecodeIcon(bytes); return bytes;
        }
        if (instance.Origin is not { } origin) return null;
        var legacy = SafePath.Within(origin.VersionDirectory, "PCL/Logo.png");
        if (!File.Exists(legacy)) return null;
        return InstanceIconImages.Normalize(await WorldContentArchive.ReadFileAsync(origin.VersionDirectory, legacy, AccountAppearancePng.MaximumPngBytes, cancellationToken), cancellationToken);
    }
    public Task SaveInstanceIconAsync(string instanceId, ReadOnlyMemory<byte> squarePng, CancellationToken cancellationToken = default)
    {
        // Own the request bytes before any await; the caller cannot mutate the confirmed image during publication.
        var owned = squarePng.ToArray();
        return ExecuteAsync("Ready", async ct =>
        {
            var pixels = AccountAppearancePng.DecodeIcon(owned);
            if (pixels.Width != 256 || pixels.Height != 256) throw new InvalidDataException("请先裁剪成 256×256 的 PNG 图标。");
            var bytes = Tools.ToolImages.EncodeRgba(256, 256, pixels.Rgba.Span); ct.ThrowIfCancellationRequested();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var instance = FindInstance(instanceId);
            VerifiedDownloader.RejectLink(Paths.LocalDataDirectory);
            var path = SafePath.Within(Paths.LocalDataDirectory, "instance-icons/" + hash + ".png"); var stage = path + ".tmp-" + Guid.NewGuid().ToString("N"); var created = false; var committed = false; var staged = false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            VerifiedDownloader.RejectLink(Path.GetDirectoryName(path)!);
            try
            {
                if (File.Exists(path))
                {
                    var previous = await WorldContentArchive.ReadFileAsync(Paths.LocalDataDirectory, path, AccountAppearancePng.MaximumPngBytes, ct);
                    if (!previous.AsSpan().SequenceEqual(bytes)) throw new IOException("已有图标缓存与内容身份不符。");
                }
                else
                {
                    await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) { staged = true; await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); }
                    ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(path); File.Move(stage, path, false); created = true;
                }
                var presentation = (instance.Presentation ?? new()) with { Icon = "custom", IconImageSha256 = hash };
                var next = Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? i with { Presentation = presentation } : i).ToArray() };
                await PersistCandidateAsync(next, ct); committed = true; Set(s => s with { Instances = next.Instances });
            }
            finally
            {
                if (staged) { VerifiedDownloader.RejectLink(stage); if (File.Exists(stage)) File.Delete(stage); }
                if (created && !committed && File.Exists(path))
                {
                    var current = await WorldContentArchive.ReadFileAsync(Paths.LocalDataDirectory, path, AccountAppearancePng.MaximumPngBytes, CancellationToken.None);
                    if (Convert.ToHexStringLower(SHA256.HashData(current)) == hash) File.Delete(path);
                }
            }
        }, cancellationToken);
    }
}
