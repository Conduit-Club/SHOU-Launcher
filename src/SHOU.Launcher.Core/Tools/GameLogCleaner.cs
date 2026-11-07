using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tools;

/// <summary>Only old, direct game log/crash files. Moves are reversible; worlds, mods and caches are not candidates.</summary>
public static class GameLogCleaner
{
    private const int MaximumFiles = 2000;
    private const long MaximumTotal = 512L * 1024 * 1024;
    private static bool Allowed(string relative)
    {
        var p = relative.Split('/');
        return p.Length == 2 && (p[0] == "logs" && (p[1].EndsWith(".log", StringComparison.OrdinalIgnoreCase) || p[1].EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase))
            || p[0] == "crash-reports" && p[1].StartsWith("crash-", StringComparison.OrdinalIgnoreCase) && p[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }
    private static string PathFor(GameInstance instance, GameLogFile file)
    {
        if (file.InstanceId != instance.Id || !Allowed(file.RelativePath) || file.Bytes is < 0 or > 64 * 1024 * 1024 || !ContentInstallFiles.Hash(file.Sha256, 64)) throw new InvalidDataException("清理记录无效，未修改文件。");
        return ContentInstallFiles.Literal(instance.GameDirectory, file.RelativePath);
    }
    private static string Trash(GameInstance instance, string id, GameLogFile file)
        => ContentInstallFiles.Literal(instance.GameDirectory, ".shou-trash/game-logs/" + id + "/" + file.RelativePath);
    private static void Validate(GameLogCleanupPlan plan)
    {
        if (!Guid.TryParseExact(plan.Id, "N", out _) || plan.Files is null || plan.Files.Count > MaximumFiles || plan.Bytes > MaximumTotal
            || plan.Files.Select(f => f.InstanceId + "/" + f.RelativePath.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count)
            throw new InvalidDataException("清理记录无效，原文件保留。");
    }
    public static async Task<GameLogCleanupPlan> ScanAsync(IReadOnlyList<GameInstance> instances, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var files = new List<GameLogFile>(); long bytes = 0;
        foreach (var instance in instances)
        foreach (var name in new[] { "logs", "crash-reports" })
        {
            var folder = ContentInstallFiles.Literal(instance.GameDirectory, name); if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder).Take(MaximumFiles + 1))
            {
                ct.ThrowIfCancellationRequested(); var relative = name + "/" + Path.GetFileName(path); if (!Allowed(relative)) continue;
                var literal = ContentInstallFiles.Literal(instance.GameDirectory, relative); var info = new FileInfo(literal);
                if (info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1) || info.Length > 64 * 1024 * 1024) continue;
                bytes += info.Length;
                if (files.Count == MaximumFiles || bytes > MaximumTotal) throw new IOException("清理预览超过 2000 文件或 512 MiB，请分批整理；原文件未修改。");
                files.Add(new(instance.Id, relative, info.Length, await HashAsync(literal, ct)));
            }
        }
        return new(Guid.NewGuid().ToString("N"), files);
    }
    public static async Task MoveAsync(GameLogCleanupPlan plan, IReadOnlyList<GameInstance> instances, string receipts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(plan); var moved = new List<(string Source, string Target)>();
        // Revalidate every file before the first move. A changed preview is never silently accepted.
        foreach (var file in plan.Files)
        {
            var instance = instances.Single(i => i.Id == file.InstanceId); var source = PathFor(instance, file);
            if (new FileInfo(source).Length != file.Bytes || await HashAsync(source, ct) != file.Sha256) throw new IOException("日志在预览后发生变化，请重新扫描。");
        }
        Directory.CreateDirectory(receipts); var receipt = ContentInstallFiles.Literal(receipts, plan.Id + ".json");
        await using (var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, plan, cancellationToken: ct);
        try
        {
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested(); var instance = instances.Single(i => i.Id == file.InstanceId);
                var source = PathFor(instance, file); var target = Trash(instance, plan.Id, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                _ = PathFor(instance, file); _ = Trash(instance, plan.Id, file);
                if (new FileInfo(source).Length != file.Bytes || await HashAsync(source, ct) != file.Sha256) throw new IOException("日志在移动前发生变化，请重新扫描。");
                File.Move(source, target, false); moved.Add((source, target));
            }
        }
        catch
        {
            foreach (var (source, target) in moved.AsEnumerable().Reverse())
            {
                VerifiedDownloader.RejectLink(source); VerifiedDownloader.RejectLink(target);
                if (!File.Exists(source)) File.Move(target, source, false);
            }
            throw;
        }
    }
    public static async Task<int> RestoreLatestAsync(IReadOnlyList<GameInstance> instances, string receipts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        VerifiedDownloader.RejectLink(receipts); if (!Directory.Exists(receipts)) return 0;
        foreach (var file in Directory.EnumerateFiles(receipts, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(1000))
        {
            VerifiedDownloader.RejectLink(file); if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
            GameLogCleanupPlan? plan;
            try { plan = JsonSerializer.Deserialize<GameLogCleanupPlan>(await File.ReadAllBytesAsync(file, ct)); if (plan is null || Path.GetFileNameWithoutExtension(file) != plan.Id) continue; Validate(plan); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { continue; }
            var restores = new List<(string Source, string Target)>();
            foreach (var entry in plan.Files)
            {
                var instance = instances.FirstOrDefault(i => i.Id == entry.InstanceId); if (instance is null) continue;
                var target = PathFor(instance, entry); var source = Trash(instance, plan.Id, entry); if (!File.Exists(source)) continue;
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("原位置已有同名日志，未覆盖；回收文件保留。");
                if (new FileInfo(source).Length != entry.Bytes || await HashAsync(source, ct) != entry.Sha256) throw new IOException("回收日志校验失败，回收文件保留。");
                restores.Add((source, target));
            }
            if (restores.Count == 0) continue;
            var moved = new List<(string Source, string Target)>();
            try
            {
                foreach (var item in restores)
                { ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(item.Source); VerifiedDownloader.RejectLink(item.Target); Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!); File.Move(item.Source, item.Target, false); moved.Add(item); }
                return moved.Count;
            }
            catch
            { foreach (var item in moved.AsEnumerable().Reverse()) { VerifiedDownloader.RejectLink(item.Target); VerifiedDownloader.RejectLink(item.Source); if (!File.Exists(item.Source)) File.Move(item.Target, item.Source, false); } throw; }
        }
        return 0;
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); }
}
