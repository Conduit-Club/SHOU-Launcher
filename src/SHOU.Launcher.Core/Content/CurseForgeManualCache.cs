using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

internal static class CurseForgeManualCache
{
    internal static string PathFor(string root, CurseForgeManualFile file)
    {
        if (!Path.IsPathFullyQualified(root) || file.ProjectId <= 0 || file.FileId <= 0 || !ContentInstallFiles.Hash(file.Sha1, 40) || file.Size is <= 0 or > PackArchiveReader.MaximumFileBytes) throw new InvalidDataException("补充文件身份无效。");
        return ContentInstallFiles.Literal(root, $"curseforge-manual/{file.ProjectId}-{file.FileId}-{file.Sha1}.bin");
    }
    internal static async Task<string?> DigestAsync(string root, CurseForgeManualFile file, CancellationToken ct)
    {
        var path = PathFor(root, file);
        if (!await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, ct).ConfigureAwait(false)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
    }
    internal static async Task StoreAsync(string root, CurseForgeManualFile file, string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("请选择已下载文件的绝对路径。");
        VerifiedDownloader.RejectLink(source);
        if (!await ContentInstallFiles.MatchesAsync(source, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new InvalidDataException("所选文件与此版本的官方 SHA-1 或大小不符，未采纳。");
        var target = PathFor(root, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = ContentInstallFiles.Literal(root, "curseforge-manual/.provide-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                if (input.Length != file.Size) throw new IOException("补充文件已改变。");
                var buffer = new byte[131072]; long copied = 0;
                while (true) { var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break; copied += count; if (copied > file.Size) throw new IOException("补充文件超过声明大小。"); await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            if (!await ContentInstallFiles.MatchesAsync(temporary, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new InvalidDataException("复制后校验失败，未采纳补充文件。");
            ct.ThrowIfCancellationRequested(); _ = PathFor(root, file); File.Move(temporary, target, true);
        }
        finally { VerifiedDownloader.RejectLink(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
