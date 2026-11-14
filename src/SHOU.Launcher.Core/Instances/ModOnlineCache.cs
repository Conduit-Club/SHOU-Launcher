using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Instances;

internal static class ModOnlineCache
{
    internal sealed record Entry(int Version, DateTimeOffset Checked, ContentRelease Current, ContentRelease? Next, ContentProject? Project);
    private static string FileName(string root, string key) => ContentInstallFiles.Literal(root, "mod-metadata/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    internal static async Task<Entry?> ReadAsync(string root, string key, CancellationToken ct)
    {
        try
        {
            var path = FileName(root, key); if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllBytesAsync(path, ct));
            return entry is { Version: 1 } && entry.Checked <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - entry.Checked < TimeSpan.FromHours(6) ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { return null; }
    }
    internal static async Task WriteAsync(string root, string key, Entry entry, CancellationToken ct)
    {
        var path = FileName(root, key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entry), ct); _ = FileName(root, key); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
