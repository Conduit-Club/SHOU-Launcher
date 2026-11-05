using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Reversible, instance-local screenshot moves. There is no permanent-delete or system-trash operation.</summary>
public static class ScreenshotTrash
{
    private const string Root = ".shou-trash/screenshots";
    private static string Literal(GameInstance instance, string relative) => ContentInstallFiles.Literal(instance.GameDirectory, relative);
    private static string Folder(GameInstance instance, string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("无效的回收记录。");
        return Literal(instance, Root + "/" + id);
    }
    private static void ValidateOriginal(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 2 || parts[0] != "screenshots" || !parts[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("仅可移动当前实例的PNG截图。");
    }
    public static async Task<IReadOnlyList<ScreenshotTrashEntry>> ListAsync(GameInstance instance, CancellationToken ct = default)
    {
        var root = Literal(instance, Root); if (!Directory.Exists(root)) return [];
        var folders = Directory.GetDirectories(root); if (folders.Length > 1000) throw new InvalidDataException("截图回收记录超过1000条，请先恢复需要的文件。");
        var result = new List<ScreenshotTrashEntry>();
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            try { var entry = await ReadAsync(instance, Path.GetFileName(folder), ct); if (File.Exists(Path.Combine(folder, "image.png"))) result.Add(entry); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { /* Damaged records are retained for manual recovery. */ }
        }
        return result.OrderByDescending(e => e.MovedAt).ToArray();
    }
    public static async Task<ScreenshotTrashEntry> MoveAsync(GameInstance instance, string relative, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateOriginal(relative); var original = Literal(instance, relative);
        var info = new FileInfo(original); if (!info.Exists || info.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("截图不存在或超过32MiB限制。");
        var root = Literal(instance, Root); Directory.CreateDirectory(root); _ = Literal(instance, Root);
        if (Directory.EnumerateDirectories(root).Take(1000).Count() >= 1000) throw new IOException("截图回收站已满，请先恢复文件。");
        var entry = new ScreenshotTrashEntry(Guid.NewGuid().ToString("N"), relative, info.Length, await DigestAsync(original, ct), DateTimeOffset.UtcNow);
        var folder = Folder(instance, entry.Id); Directory.CreateDirectory(folder); var moved = false;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(entry), ct);
            ct.ThrowIfCancellationRequested(); _ = Literal(instance, relative); _ = Folder(instance, entry.Id);
            if (new FileInfo(original).Length != entry.Bytes || await DigestAsync(original, ct) != entry.Sha256) throw new IOException("截图在移动前已变化，请刷新。");
            File.Move(original, Path.Combine(folder, "image.png"), false); moved = true; return entry;
        }
        finally { if (!moved && Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    public static async Task RestoreAsync(GameInstance instance, string id, CancellationToken ct = default)
    {
        var entry = await ReadAsync(instance, id, ct); ValidateOriginal(entry.RelativePath);
        var folder = Folder(instance, id); var image = Literal(instance, Root + "/" + id + "/image.png"); var original = Literal(instance, entry.RelativePath);
        if (new FileInfo(image).Length != entry.Bytes || await DigestAsync(image, ct) != entry.Sha256) throw new InvalidDataException("回收截图校验失败，保留回收文件。");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!); _ = Literal(instance, entry.RelativePath);
        if (File.Exists(original) || Directory.Exists(original) || Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(original)!).Any(f => Path.GetFileName(f).Equals(Path.GetFileName(original), StringComparison.OrdinalIgnoreCase))) throw new IOException("原位置已有同名文件，未覆盖。");
        ct.ThrowIfCancellationRequested(); File.Move(image, original, false);
        File.Delete(Path.Combine(folder, "receipt.json")); if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
    private static async Task<ScreenshotTrashEntry> ReadAsync(GameInstance instance, string id, CancellationToken ct)
    {
        _ = Folder(instance, id); var receipt = Literal(instance, Root + "/" + id + "/receipt.json");
        if (new FileInfo(receipt).Length > 16384) throw new InvalidDataException("回收记录过大。");
        var entry = JsonSerializer.Deserialize<ScreenshotTrashEntry>(await File.ReadAllBytesAsync(receipt, ct));
        if (entry is null || entry.Id != id || entry.Bytes is <= 0 or > 32 * 1024 * 1024 || !ContentInstallFiles.Hash(entry.Sha256, 64)) throw new InvalidDataException("回收记录无效。");
        ValidateOriginal(entry.RelativePath); _ = Literal(instance, entry.RelativePath); _ = Literal(instance, Root + "/" + id + "/image.png"); return entry;
    }
    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)); }
}
