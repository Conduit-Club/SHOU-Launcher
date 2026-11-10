using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

public sealed partial class InstanceWorldStore
{
    private sealed record RecycledPack(string Name, bool Directory, string Hash);
    private sealed record PackTrashJournal(int Format, string Id, DateTimeOffset Created, string LevelBefore, string LevelAfter,
        string ManifestBefore, string ManifestAfter, IReadOnlyList<RecycledPack> Packs);
    private const int TrashMetadataLimit = 1024 * 1024;
    private string TrashRoot(string world) => WorldChild(world, ".shou-trash/datapacks");
    private string TrashPath(string world, string id) => Path.Combine(TrashRoot(world), TrashId(id));
    private static string TrashId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("回收记录标识无效。");
    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private void EnsureTrashDirectory(string path)
    {
        var guard = Capture(path, true); guard.VerifyExistingNodes(); Directory.CreateDirectory(path); guard.VerifyExistingNodes();
        if (!Capture(path).Final.Directory) throw new IOException("回收路径不是文件夹。");
    }
    private async Task SaveTrashFileAsync(string directory, string name, byte[] bytes, CancellationToken ct) =>
        _ = await WriteNewAsync(Path.Combine(directory, name), bytes, Capture(directory), ct, null);
    private async Task<string> PackFingerprintAsync(string path, bool directory, CancellationToken ct)
    {
        var guard = Capture(path);
        if (guard.Final.Directory != directory) throw new IOException("数据包文件类型已改变。");
        var hash = directory ? (await WorldContentArchive.FolderDatapackAsync(path, new(KnownPackFormat, 0), ct)).Revision
            : Revision(await ReadGuardedAsync(guard, limits.MaxPackBytes, ct));
        guard.Verify(); return hash;
    }
    private byte[] WithoutPackReferences(EditableWorld world, IReadOnlySet<string> names, CancellationToken ct)
    {
        var packs = world.Data.Find("DataPacks")!.Children.ToList();
        Replace(packs, StringList("Enabled", world.Enabled!.Where(p => !names.Contains(p)).ToList()));
        Replace(packs, StringList("Disabled", world.Disabled!.Where(p => !names.Contains(p)).ToList()));
        var data = world.Data.Children.ToList(); Replace(data, world.Data.Find("DataPacks")! with { Value = packs });
        var root = world.Document.Root.Children.ToList(); Replace(root, world.Data with { Value = data });
        return BoundedNbt.Write(world.Document with { Root = world.Document.Root with { Value = root } }, nbtLimits, ct);
    }
    private static byte[]? WithoutManifestPacks(byte[]? original, IReadOnlyList<RecycledPack> selected)
    {
        if (original is null) return null;
        var json = JsonNode.Parse(original, documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("安装清单无效，未回收文件。");
        if ((int?)json["FormatVersion"] != 1 || json["Files"] is not JsonArray files || files.Count > 4096) throw new InvalidDataException("安装清单格式未验证，未回收文件。");
        for (var index = files.Count - 1; index >= 0; index--)
        {
            var path = (string?)files[index]?["RelativePath"] ?? throw new InvalidDataException("安装清单缺少文件路径。");
            var key = ContentInstallFiles.PathKey(path);
            if (selected.Any(p => key == ContentInstallFiles.PathKey("datapacks/" + p.Name) || p.Directory && key.StartsWith(ContentInstallFiles.PathKey("datapacks/" + p.Name + "/"), StringComparison.Ordinal))) files.RemoveAt(index);
        }
        return JsonSerializer.SerializeToUtf8Bytes(json);
    }
    public async Task RecycleDatapacksAsync(string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackSelection> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count is < 1 or > 16 || selected.Select(p => p.PackId).Distinct(StringComparer.Ordinal).Count() != selected.Count) throw new InvalidDataException("请选择 1 至 16 个不同的数据包。");
        await mutation.WaitAsync(ct);
        try
        {
            var world = await ReadEditableWorldAsync(worldDirectoryName, ct); RequireWorldRevision(world, expectedRevision);
            if (!world.KnownVersion || world.Enabled is null || world.Disabled is null) throw new InvalidOperationException("此世界的版本或数据包状态尚未验证，仅支持只读操作。");
            var files = await GetDatapacksAsync(worldDirectoryName, ct); var packs = new List<RecycledPack>(); var guards = new List<WorldPathSnapshot>(); var budget = new PackReadBudget();
            foreach (var selection in selected)
            {
                if (!selection.PackId.StartsWith("file/", StringComparison.Ordinal)) throw new InvalidDataException("只能回收明确选择的本地数据包。");
                var name = Leaf(selection.PackId[5..]); var file = files.SingleOrDefault(f => f.Name == name) ?? throw new IOException("所选数据包已经缺失。");
                var inspected = await InspectPackAsync(worldDirectoryName, file, budget, ct);
                if (!inspected.ToggleSafe || inspected.Revision != selection.ContentRevision) throw new IOException("数据包内容或兼容状态已改变，请重新读取。");
                guards.AddRange(inspected.Guards);
                packs.Add(new(name, file.IsDirectory, await PackFingerprintAsync(WorldChild(worldDirectoryName, "datapacks/" + name), file.IsDirectory, ct)));
            }
            var manifestPath = WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName);
            var manifest = await ReadOptionalAsync(manifestPath, TrashMetadataLimit, ct);
            var nextManifest = WithoutManifestPacks(manifest, packs);
            var nextLevel = WithoutPackReferences(world, selected.Select(p => p.PackId).ToHashSet(StringComparer.Ordinal), ct);
            var trashRoot = TrashRoot(worldDirectoryName); var existingTrash = Capture(trashRoot, true);
            if (existingTrash.Exists && Directory.EnumerateDirectories(trashRoot).Take(256).Count() >= 256) throw new IOException("回收记录已达 256 项，请先备份并整理存档回收目录，未移动数据包。");
            var id = Guid.NewGuid().ToString("N"); var directory = TrashPath(worldDirectoryName, id);
            EnsureTrashDirectory(directory); EnsureTrashDirectory(Path.Combine(directory, "payload"));
            var journal = new PackTrashJournal(1, id, DateTimeOffset.UtcNow, Revision(world.Bytes), Revision(nextLevel), Revision(manifest), Revision(nextManifest), packs);
            await SaveTrashFileAsync(directory, "level.before", world.Bytes, ct); await SaveTrashFileAsync(directory, "level.after", nextLevel, ct);
            if (manifest is not null) { await SaveTrashFileAsync(directory, "manifest.before", manifest, ct); await SaveTrashFileAsync(directory, "manifest.after", nextManifest!, ct); }
            await SaveTrashFileAsync(directory, "receipt.json", JsonSerializer.SerializeToUtf8Bytes(journal), ct);
            foreach (var guard in guards) guard.Verify();
            await ApplyTrashTransactionAsync(worldDirectoryName, directory, journal, world.Bytes, nextLevel, manifest, nextManifest, restore: false, ct);
        }
        finally { mutation.Release(); }
    }
    private async Task<(PackTrashJournal Journal, byte[] Before, byte[] After, byte[]? ManifestBefore, byte[]? ManifestAfter)> ReadTrashAsync(string world, string directory, CancellationToken ct)
    {
        var journal = JsonSerializer.Deserialize<PackTrashJournal>(await ReadOptionalAsync(Path.Combine(directory, "receipt.json"), 65536, ct) ?? throw new IOException("回收记录缺失。")) ?? throw new InvalidDataException("回收记录无效。");
        if (journal.Format != 1 || journal.Id != Path.GetFileName(directory) || journal.Packs is null || journal.Packs.Count is < 1 or > 16 || journal.Packs.Any(p => p is null || p.Name is null || p.Hash is null) || journal.Packs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Packs.Count) throw new InvalidDataException("回收记录超出边界。");
        foreach (var pack in journal.Packs) { _ = Leaf(pack.Name); if (pack.Hash.Length != 64 || !pack.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("回收文件哈希无效。"); }
        var before = await ReadOptionalAsync(Path.Combine(directory, "level.before"), nbtLimits.MaxInputBytes, ct) ?? throw new IOException("回收备份缺失。");
        var after = await ReadOptionalAsync(Path.Combine(directory, "level.after"), nbtLimits.MaxInputBytes, ct) ?? throw new IOException("回收状态缺失。");
        var mb = await ReadOptionalAsync(Path.Combine(directory, "manifest.before"), TrashMetadataLimit, ct); var ma = await ReadOptionalAsync(Path.Combine(directory, "manifest.after"), TrashMetadataLimit, ct);
        if (Revision(before) != journal.LevelBefore || Revision(after) != journal.LevelAfter || Revision(mb) != journal.ManifestBefore || Revision(ma) != journal.ManifestAfter) throw new IOException("回收备份校验失败，原文件保留。");
        _ = BoundedNbt.Read(before, nbtLimits, ct); _ = BoundedNbt.Read(after, nbtLimits, ct);
        return (journal, before, after, mb, ma);
    }
    public async Task<IReadOnlyList<WorldDatapackTrashEntry>> GetDatapackTrashAsync(string worldDirectoryName, CancellationToken ct = default)
    {
        var root = TrashRoot(worldDirectoryName); var guard = Capture(root, true); if (!guard.Exists) return [];
        var level = await ReadOptionalAsync(WorldChild(worldDirectoryName, "level.dat"), nbtLimits.MaxInputBytes, ct);
        var manifest = await ReadOptionalAsync(WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName), TrashMetadataLimit, ct);
        var entries = Directory.EnumerateDirectories(root).Take(257).ToArray(); if (entries.Length > 256) throw new IOException("回收记录过多，请先整理。");
        var result = new List<WorldDatapackTrashEntry>();
        foreach (var directory in entries)
        {
            ct.ThrowIfCancellationRequested(); _ = TrashId(Path.GetFileName(directory)); _ = Capture(directory);
            if (Capture(Path.Combine(directory, "restored"), true).Exists || Capture(Path.Combine(directory, "rolled-back"), true).Exists) continue;
            if (!Capture(Path.Combine(directory, "receipt.json"), true).Exists) continue;
            var item = await ReadTrashAsync(worldDirectoryName, directory, ct);
            var ready = Capture(Path.Combine(directory, "ready"), true).Exists;
            var unchanged = Revision(level) == item.Journal.LevelAfter && Revision(manifest) == item.Journal.ManifestAfter;
            result.Add(new(item.Journal.Id, item.Journal.Packs.Select(p => p.Name).ToArray(), item.Journal.Created, ready && unchanged,
                !ready ? "操作未完成，恢复文件仍保留在存档回收目录，请先核对。" : !unchanged ? "存档或安装清单已有其它修改，禁止覆盖恢复；回收文件仍保留。" : "可恢复"));
        }
        guard.Verify(); return result.OrderByDescending(e => e.Created).ToArray();
    }
    public async Task RestoreDatapacksAsync(string worldDirectoryName, string trashId, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            var directory = TrashPath(worldDirectoryName, trashId); var item = await ReadTrashAsync(worldDirectoryName, directory, ct);
            if (!Capture(Path.Combine(directory, "ready"), true).Exists || Capture(Path.Combine(directory, "restored"), true).Exists) throw new IOException("此记录当前不可恢复。");
            await ApplyTrashTransactionAsync(worldDirectoryName, directory, item.Journal, item.Before, item.After, item.ManifestBefore, item.ManifestAfter, restore: true, ct);
        }
        finally { mutation.Release(); }
    }
    private async Task ApplyTrashTransactionAsync(string world, string directory, PackTrashJournal journal, byte[] before, byte[] after, byte[]? manifestBefore, byte[]? manifestAfter, bool restore, CancellationToken ct)
    {
        var levelPath = WorldChild(world, "level.dat"); var manifestPath = WorldChild(world, ContentInstallFiles.ManifestName);
        var oldLevel = restore ? after : before; var newLevel = restore ? before : after;
        var oldManifest = restore ? manifestAfter : manifestBefore; var newManifest = restore ? manifestBefore : manifestAfter;
        var moved = new List<(string From, string To, RecycledPack Pack)>();
        async Task RequireMetadataAsync(CancellationToken token)
        {
            if (!SameBytes(await ReadOptionalAsync(levelPath, nbtLimits.MaxInputBytes, token), oldLevel) || !SameBytes(await ReadOptionalAsync(manifestPath, TrashMetadataLimit, token), oldManifest)) throw new IOException("存档或安装清单已改变，未覆盖当前状态。");
        }
        await RequireMetadataAsync(ct);
        var transfers = new List<(string From, string To, RecycledPack Pack, WorldPathSnapshot Guard)>();
        foreach (var pack in journal.Packs)
        {
            var original = WorldChild(world, "datapacks/" + pack.Name); var recycled = Path.Combine(directory, "payload", pack.Name);
            var from = restore ? recycled : original; var to = restore ? original : recycled;
            RequireVacantPackPath(to); var source = Capture(from);
            if (await PackFingerprintAsync(from, pack.Directory, ct) != pack.Hash) throw new IOException("数据包内容已改变，操作已停止。");
            transfers.Add((from, to, pack, source));
        }
        try
        {
            foreach (var transfer in transfers)
            {
                ct.ThrowIfCancellationRequested(); transfer.Guard.Verify(); RequireVacantPackPath(transfer.To);
                moved.Add((transfer.From, transfer.To, transfer.Pack)); MovePack(transfer.From, transfer.To, transfer.Pack.Directory);
                if (await PackFingerprintAsync(transfer.To, transfer.Pack.Directory, ct) != transfer.Pack.Hash) throw new IOException("移动后的数据包校验失败。");
            }
            await RequireMetadataAsync(ct);
            if (!SameBytes(oldLevel, newLevel)) await AtomicWriteAsync(levelPath, newLevel, oldLevel, ct);
            if (!SameBytes(oldManifest, newManifest)) await AtomicWriteAsync(manifestPath, newManifest!, oldManifest, ct);
            ct.ThrowIfCancellationRequested();
            await SaveTrashFileAsync(directory, restore ? "restored" : "ready", [], ct);
        }
        catch (Exception error)
        {
            try
            {
                await RollbackMetadataAsync(manifestPath, oldManifest, newManifest, TrashMetadataLimit);
                await RollbackMetadataAsync(levelPath, oldLevel, newLevel, nbtLimits.MaxInputBytes);
                foreach (var transfer in moved.AsEnumerable().Reverse())
                {
                    if (!Capture(transfer.To, true).Exists && Capture(transfer.From, true).Exists)
                    {
                        if (await PackFingerprintAsync(transfer.From, transfer.Pack.Directory, CancellationToken.None) != transfer.Pack.Hash) throw new IOException("回滚源文件已改变。");
                        continue;
                    }
                    RequireVacantPackPath(transfer.From);
                    if (await PackFingerprintAsync(transfer.To, transfer.Pack.Directory, CancellationToken.None) != transfer.Pack.Hash) throw new IOException("回滚文件已被修改。");
                    MovePack(transfer.To, transfer.From, transfer.Pack.Directory);
                }
                if (!restore) await SaveTrashFileAsync(directory, "rolled-back", [], CancellationToken.None);
            }
            catch (Exception rollback) { throw new IOException("操作及自动回滚未完成；恢复文件与收据保留在存档 .shou-trash/datapacks 中，禁止覆盖原位置。", new AggregateException(error, rollback)); }
            throw;
        }
    }
    private async Task RollbackMetadataAsync(string path, byte[]? original, byte[]? output, int maximum)
    {
        if (SameBytes(original, output)) return;
        var current = await ReadOptionalAsync(path, maximum, CancellationToken.None);
        if (SameBytes(current, original)) return;
        if (!SameBytes(current, output) || original is null) throw new IOException("回滚目标已有其它修改，文件保留。");
        await AtomicWriteAsync(path, original, current, CancellationToken.None);
    }
    private void RequireVacantPackPath(string path)
    {
        var guard = Capture(path, true); if (guard.Exists) throw new IOException("目标已有同名文件，未覆盖。");
        var parent = Path.GetDirectoryName(path)!; var leaf = ContentInstallFiles.PathKey(Path.GetFileName(path));
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent)) if (ContentInstallFiles.PathKey(Path.GetFileName(entry)) == leaf) throw new IOException("目标存在大小写或 Unicode 等价的同名文件，未覆盖。");
        guard.Verify();
    }
    private void MovePack(string from, string to, bool directory)
    {
        var source = Capture(from); var target = Capture(to, true); var parent = Capture(Path.GetDirectoryName(to)!);
        source.Verify(); target.Verify(); parent.Verify();
        if (directory) Directory.Move(from, to); else File.Move(from, to, false);
        parent.Verify(); if (!Capture(to).Final.SameNode(source.Final)) throw new IOException("移动文件的身份已改变。");
    }
}
