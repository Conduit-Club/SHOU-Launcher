using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Instances;

internal static class ModFileManagement
{
    private sealed record TrashRecord(int Version, string InstanceId, DateTimeOffset Created,
        IReadOnlyList<ContentInstalledFile> Files, IReadOnlyList<ContentInstalledFile> RemovedManifestFiles);

    private static string FolderName(string kind) => kind switch { "mods" or "resourcepacks" or "schematics" => kind, "shaders" => "shaderpacks", _ => throw new ArgumentException("不支持此资源回收类别。") };
    private static string PathOf(GameInstance instance, string relative, string kind = "mods")
    {
        var folder = FolderName(kind);
        if (!relative.StartsWith(folder + "/", StringComparison.Ordinal) || kind != "schematics" && relative[(folder.Length + 1)..].Contains('/') || relative.Contains('\\') || !InstanceWorkspace.Accepts(kind, relative))
            throw new InvalidDataException("资源路径无效。");
        return ContentInstallFiles.Literal(instance.GameDirectory, relative);
    }
    private static string EnabledPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    private static void ValidatePaths(GameInstance instance, IReadOnlyList<string> paths, string kind = "mods")
    {
        if (paths.Count == 0 || paths.Select(ContentInstallFiles.PathKey).Distinct(StringComparer.Ordinal).Count() != paths.Count) throw new InvalidDataException("请选择不重复的资源文件。");
        foreach (var path in paths) if (!File.Exists(PathOf(instance, path, kind))) throw new IOException("所选资源不存在，请刷新列表。");
    }
    public static void Toggle(GameInstance instance, IReadOnlyList<string> paths, bool enabled, Action validateTarget, CancellationToken ct)
    {
        ValidatePaths(instance, paths);
        var plans = paths.Select(p => (Old: p, Next: enabled ? EnabledPath(p) : EnabledPath(p) + ".disabled")).Where(p => p.Old != p.Next).ToArray();
        if (plans.Select(p => ContentInstallFiles.PathKey(p.Next)).Distinct(StringComparer.Ordinal).Count() != plans.Length || plans.Any(p => File.Exists(PathOf(instance, p.Next)))) throw new IOException("模组目标名称冲突，未修改文件。");
        var moved = new List<(string Old, string Next)>();
        try
        {
            foreach (var plan in plans) { ct.ThrowIfCancellationRequested(); validateTarget(); File.Move(PathOf(instance, plan.Old), PathOf(instance, plan.Next), false); moved.Add(plan); }
        }
        catch
        {
            foreach (var plan in moved.AsEnumerable().Reverse()) File.Move(PathOf(instance, plan.Next), PathOf(instance, plan.Old), false);
            throw;
        }
    }
    private static string TrashRoot(GameInstance instance, string kind = "mods") => ContentInstallFiles.Literal(instance.GameDirectory, kind == "mods" ? ".shou-mod-trash" : ".shou-content-trash/" + FolderName(kind));
    private static string Receipt(GameInstance instance, string id, string kind = "mods")
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("回收记录身份无效。");
        return ContentInstallFiles.Literal(TrashRoot(instance, kind), id + "/receipt.json");
    }
    private static Task<byte[]?> ManifestBytes(GameInstance instance, CancellationToken ct)
    {
        var path = ContentInstallFiles.Literal(instance.GameDirectory, ContentInstallFiles.ManifestName);
        return File.Exists(path) ? Read() : Task.FromResult<byte[]?>(null);
        async Task<byte[]?> Read() => await File.ReadAllBytesAsync(path, ct);
    }
    private static async Task WriteManifestAsync(GameInstance instance, ContentInstallManifest manifest, byte[]? expected, CancellationToken ct)
    {
        var target = ContentInstallFiles.Literal(instance.GameDirectory, ContentInstallFiles.ManifestName);
        var temporary = ContentInstallFiles.Literal(instance.GameDirectory, ".mod-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temporary, ContentInstallFiles.SerializeManifest(manifest), ct); ct.ThrowIfCancellationRequested();
            var current = await ManifestBytes(instance, ct);
            if (!(current is null ? expected is null : expected is not null && current.AsSpan().SequenceEqual(expected))) throw new IOException("安装清单已被外部改变，未覆盖。");
            _ = ContentInstallFiles.Literal(instance.GameDirectory, ContentInstallFiles.ManifestName); File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<InstanceModTrash> TrashAsync(GameInstance instance, IReadOnlyList<string> paths, Action validateTarget, CancellationToken ct, string kind = "mods")
    {
        ValidatePaths(instance, paths, kind); var expected = await ManifestBytes(instance, ct); var before = await ContentInstallFiles.ReadManifestAsync(instance, ct);
        var removed = before.Files.Where(f => paths.Any(p => EnabledPath(p) == EnabledPath(f.RelativePath))).ToArray();
        var id = Guid.NewGuid().ToString("N"); var receipt = Receipt(instance, id, kind); var root = Path.GetDirectoryName(receipt)!;
        var files = new List<ContentInstalledFile>();
        foreach (var path in paths)
        {
            await using var stream = new FileStream(PathOf(instance, path, kind), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct)); stream.Position = 0;
            files.Add(new("local", "local", kind switch { "mods" => "mod", "resourcepacks" => "resourcepack", "shaders" => "shader", _ => "schematic" }, path, sha1, Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct)), stream.Length));
        }
        var record = new TrashRecord(1, instance.Id, DateTimeOffset.UtcNow, files, removed); var moved = new List<ContentInstalledFile>();
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested(); validateTarget();
                if (!await ContentInstallFiles.MatchesAsync(PathOf(instance, file.RelativePath, kind), file.Sha1, file.Size, ct)) throw new IOException("资源在回收前发生变化。");
                var target = ContentInstallFiles.Literal(root, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(PathOf(instance, file.RelativePath, kind), target, false); moved.Add(file);
            }
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(record), ct);
            ct.ThrowIfCancellationRequested(); validateTarget();
            if (kind == "mods" || removed.Length > 0) await WriteManifestAsync(instance, before with { Files = before.Files.Except(removed).ToArray() }, expected, ct);
            return new(id, files.Count, record.Created);
        }
        catch
        {
            foreach (var file in moved.AsEnumerable().Reverse()) File.Move(ContentInstallFiles.Literal(root, file.RelativePath), PathOf(instance, file.RelativePath, kind), false);
            if (File.Exists(receipt)) File.Delete(receipt); throw;
        }
    }
    private static async Task<TrashRecord> ReadAsync(GameInstance instance, string id, CancellationToken ct, string kind = "mods")
    {
        var path = Receipt(instance, id, kind);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("回收记录无效。");
        var record = JsonSerializer.Deserialize<TrashRecord>(await File.ReadAllTextAsync(path, ct)) ?? throw new InvalidDataException("回收记录无效。");
        if (record.Version != 1 || record.InstanceId != instance.Id || record.Files is not { Count: > 0 } || record.RemovedManifestFiles is null || record.Files.Any(f => f is null) || record.RemovedManifestFiles.Any(f => f is null)) throw new InvalidDataException("回收记录不属于当前实例。");
        if (record.Files.Select(f => ContentInstallFiles.PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != record.Files.Count) throw new InvalidDataException("回收文件重复。");
        foreach (var file in record.Files.Concat(record.RemovedManifestFiles))
        {
            _ = PathOf(instance, file.RelativePath, kind);
            if (!ContentInstallFiles.Hash(file.Sha1, 40) || !ContentInstallFiles.Hash(file.Sha512, 128) || file.Size < 0) throw new InvalidDataException("回收文件身份无效。");
        }
        return record;
    }
    public static async Task<IReadOnlyList<InstanceModTrash>> ListAsync(GameInstance instance, CancellationToken ct, string kind = "mods")
    {
        var root = TrashRoot(instance, kind); var result = new List<InstanceModTrash>();
        if (!Directory.Exists(root)) return result;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            try { var id = Path.GetFileName(folder); var record = await ReadAsync(instance, id, ct, kind); result.Add(new(id, record.Files.Count, record.Created)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        }
        return result.OrderByDescending(r => r.Created).ToArray();
    }
    public static async Task RestoreAsync(GameInstance instance, string id, Action validateTarget, CancellationToken ct, string kind = "mods")
    {
        var record = await ReadAsync(instance, id, ct, kind); var root = Path.GetDirectoryName(Receipt(instance, id, kind))!;
        var expected = await ManifestBytes(instance, ct); var before = await ContentInstallFiles.ReadManifestAsync(instance, ct);
        foreach (var file in record.Files)
        {
            if (File.Exists(PathOf(instance, file.RelativePath, kind)) || File.Exists(PathOf(instance, EnabledPath(file.RelativePath), kind)) || File.Exists(PathOf(instance, EnabledPath(file.RelativePath) + ".disabled", kind))) throw new IOException("恢复目标已有同名资源，原文件保留。");
            var path = ContentInstallFiles.Literal(root, file.RelativePath);
            if (!await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, ct)) throw new IOException("回收文件已改变，未恢复。");
            await using var stream = File.OpenRead(path); if (Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct)) != file.Sha512) throw new IOException("回收文件哈希不符。");
        }
        if (record.RemovedManifestFiles.Any(f => before.Files.Any(n => EnabledPath(n.RelativePath) == EnabledPath(f.RelativePath)))) throw new IOException("安装清单出现同名资源，请先核对。");
        var moved = new List<ContentInstalledFile>(); var receiptMoved = false; var restoredReceipt = ContentInstallFiles.Literal(root, "restored.json");
        try
        {
            foreach (var file in record.Files) { ct.ThrowIfCancellationRequested(); validateTarget(); var target = PathOf(instance, file.RelativePath, kind); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(ContentInstallFiles.Literal(root, file.RelativePath), target, false); moved.Add(file); }
            File.Move(Receipt(instance, id, kind), restoredReceipt, false); receiptMoved = true;
            validateTarget(); if (kind == "mods" || record.RemovedManifestFiles.Count > 0) await WriteManifestAsync(instance, before with { Files = before.Files.Concat(record.RemovedManifestFiles).ToArray() }, expected, ct);
        }
        catch { if (receiptMoved) File.Move(restoredReceipt, Receipt(instance, id, kind), false); foreach (var file in moved.AsEnumerable().Reverse()) File.Move(PathOf(instance, file.RelativePath, kind), ContentInstallFiles.Literal(root, file.RelativePath), false); throw; }
    }
    public static async Task ReplaceImportedAsync(GameInstance instance, string relative, string temporary, CancellationToken ct)
    {
        var target = PathOf(instance, relative); var expected = await ManifestBytes(instance, ct); var manifest = await ContentInstallFiles.ReadManifestAsync(instance, ct);
        var backup = ContentInstallFiles.Literal(instance.GameDirectory, ".shou-mod-backups/local-import-" + Guid.NewGuid().ToString("N"));
        var original = ContentInstallFiles.Literal(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        if (expected is not null) await File.WriteAllBytesAsync(ContentInstallFiles.Literal(backup, ContentInstallFiles.ManifestName), expected, ct);
        string oldHash, newHash;
        await using (var input = File.OpenRead(target)) oldHash = Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct));
        await using (var input = File.OpenRead(temporary)) newHash = Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct));
        var moved = false; var published = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var input = File.OpenRead(PathOf(instance, relative))) if (Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct)) != oldHash) throw new IOException("原模组在替换前发生变化。");
            File.Move(target, original, false); moved = true;
            _ = PathOf(instance, relative); ct.ThrowIfCancellationRequested(); File.Move(temporary, target, false); published = true;
            var next = manifest with { Files = manifest.Files.Where(f => EnabledPath(f.RelativePath) != EnabledPath(relative)).ToArray() };
            if (next.Files.Count != manifest.Files.Count) await WriteManifestAsync(instance, next, expected, ct);
        }
        catch
        {
            if (published)
            {
                await using (var input = File.OpenRead(PathOf(instance, relative))) if (Convert.ToHexStringLower(await SHA512.HashDataAsync(input)) != newHash) throw new IOException("替换回滚目标被外部改变，原模组保留在：" + backup);
                File.Delete(target);
            }
            if (moved) File.Move(original, PathOf(instance, relative), false); throw;
        }
    }
}
