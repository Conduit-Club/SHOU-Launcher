using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Instances;

internal sealed record DatapackUpdateJournalFile(string? OldName, string NewName, string Sha256, string? OldHash, bool OldDirectory, bool Enable);
internal sealed record DatapackUpdateJournal(int Format, string World, DateTimeOffset Created, string OriginalRevision, string AfterRevision,
    string LevelBefore, string LevelAfter, string ManifestBefore, string ManifestAfter, IReadOnlyList<DatapackUpdateJournalFile> Files);
internal sealed record DatapackRestoreState(string Revision, string WorkspaceRevision, string Directory, DatapackUpdateJournal Journal,
    byte[] LevelBefore, byte[] LevelAfter, byte[]? ManifestBefore, byte[]? ManifestAfter, IReadOnlyDictionary<string, ManagedDatapack> Originals);

public sealed partial class InstanceWorldStore
{
    private const int DatapackJournalLimit = 256 * 1024;
    private string DatapackBackupRoot(string world) => WorldChild(world, ".shou-backups/datapack-updates");
    private string DatapackBackupPath(string world, string id) => Path.Combine(DatapackBackupRoot(world), TrashId(id));
    public string GetDatapackBackupDirectory(string world, string id)
    { var path = DatapackBackupPath(world, id); var guard = Capture(path); if (!guard.Final.Directory) throw new IOException("备份目录不存在。"); guard.Verify(); return path; }
    private async Task<(DatapackUpdateJournal Journal, byte[] Bytes)> ReadDatapackJournalAsync(string world, string directory, CancellationToken ct)
    {
        var bytes = await ReadOptionalAsync(Path.Combine(directory, "receipt.json"), DatapackJournalLimit, ct) ?? throw new IOException("更新备份缺少收据。");
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        if (json.RootElement.GetProperty("Format").GetInt32() != 2) throw new NotSupportedException("旧版备份缺少完整恢复快照，仅保留供手动核对。");
        var journal = JsonSerializer.Deserialize<DatapackUpdateJournal>(bytes, new JsonSerializerOptions { MaxDepth = 16 }) ?? throw new InvalidDataException("更新备份收据无效。");
        if (journal.World != world || journal.Files is null || journal.Files.Count is < 1 or > 64 || journal.Files.Any(f => f is null || f.NewName is null || !ContentInstallFiles.Hash(f.Sha256, 64) || f.OldName is not null && !ContentInstallFiles.Hash(f.OldHash, 64)) ||
            new[] { journal.OriginalRevision, journal.AfterRevision, journal.LevelBefore, journal.LevelAfter }.Any(h => !ContentInstallFiles.Hash(h, 64)) ||
            new[] { journal.ManifestBefore, journal.ManifestAfter }.Any(h => h != "missing" && !ContentInstallFiles.Hash(h, 64)) || (journal.ManifestBefore == "missing") != (journal.ManifestAfter == "missing")) throw new InvalidDataException("更新备份收据字段无效。");
        foreach (var file in journal.Files) { _ = Leaf(file.NewName); if (!file.NewName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件名无效。"); if (file.OldName is { } old) _ = Leaf(old); }
        if (journal.Files.Select(f => ContentInstallFiles.PathKey(f.NewName)).Distinct().Count() != journal.Files.Count || journal.Files.Where(f => f.OldName is not null).Select(f => ContentInstallFiles.PathKey(f.OldName!)).Distinct().Count() != journal.Files.Count(f => f.OldName is not null)) throw new InvalidDataException("更新备份包含重复名称。");
        return (journal, bytes);
    }
    public async Task<IReadOnlyList<WorldDatapackBackupEntry>> GetDatapackBackupsAsync(string world, CancellationToken ct = default)
    {
        var state = await ReadDatapackWorkspaceAsync(world, ct); var root = DatapackBackupRoot(world); var guard = Capture(root, true); if (!guard.Exists) return [];
        var directories = Directory.EnumerateDirectories(root).Take(257).ToArray(); if (directories.Length > 256) throw new IOException("更新备份超过 256 条，请先备份整理。");
        var entries = new List<WorldDatapackBackupEntry>();
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested(); var id = Path.GetFileName(directory); if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                _ = Capture(directory); var (item, _) = await ReadDatapackJournalAsync(world, directory, ct);
                var restored = Capture(Path.Combine(directory, "restored"), true).Exists;
                var completed = Capture(Path.Combine(directory, "complete"), true).Exists && !Capture(Path.Combine(directory, "rolled-back"), true).Exists;
                var unchanged = item.AfterRevision == state.Revision;
                entries.Add(new(id, item.Created, item.Files.Select(f => f.OldName ?? f.NewName).ToArray(), completed && !restored && unchanged,
                    restored ? "已恢复；原备份与替换版本仍保留。" : !completed ? "更新未完成；仅保留恢复资料，未允许自动恢复。" : !unchanged ? "存档、数据包或来源清单已有后续修改，禁止覆盖恢复。" : "可检查恢复"));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException or ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
            { entries.Add(new(id, DateTimeOffset.MinValue, [id[..8]], false, ex is NotSupportedException ? ex.Message : "备份记录损坏或不可读取，文件保留，请先手动核对。")); }
        }
        guard.Verify(); return entries.OrderByDescending(e => e.Created).ToArray();
    }
    internal async Task<DatapackRestoreState> PrepareDatapackRestoreAsync(string world, string id, CancellationToken ct)
    {
        var directory = GetDatapackBackupDirectory(world, id); var (journal, receipt) = await ReadDatapackJournalAsync(world, directory, ct);
        if (!Capture(Path.Combine(directory, "complete"), true).Exists || Capture(Path.Combine(directory, "rolled-back"), true).Exists || Capture(Path.Combine(directory, "restored"), true).Exists) throw new IOException("此备份未完成或已经恢复，未重复覆盖。");
        var state = await ReadDatapackWorkspaceAsync(world, ct); if (state.Revision != journal.AfterRevision) throw new IOException("存档、数据包或来源清单已有后续修改，未覆盖恢复。");
        var before = await ReadOptionalAsync(Path.Combine(directory, "level.before"), nbtLimits.MaxInputBytes, ct) ?? throw new IOException("原存档快照缺失。");
        var after = await ReadOptionalAsync(Path.Combine(directory, "level.after"), nbtLimits.MaxInputBytes, ct) ?? throw new IOException("更新后快照缺失。");
        var mb = await ReadOptionalAsync(Path.Combine(directory, "manifest.before"), 4 * 1024 * 1024, ct); var ma = await ReadOptionalAsync(Path.Combine(directory, "manifest.after"), 4 * 1024 * 1024, ct);
        if (Revision(before) != journal.LevelBefore || Revision(after) != journal.LevelAfter || Revision(mb) != journal.ManifestBefore || Revision(ma) != journal.ManifestAfter) throw new IOException("备份元数据校验失败。");
        var originalWorld = ParseEditableWorld(before, ct); var afterWorld = ParseEditableWorld(after, ct);
        if (ManagementFormat(originalWorld) != state.Format || originalWorld.Version != state.Version || originalWorld.DataVersion != state.DataVersion || afterWorld.Version != state.Version || afterWorld.DataVersion != state.DataVersion || originalWorld.Enabled is null || originalWorld.Disabled is null) throw new InvalidDataException("备份与当前世界版本或格式不符。");
        var originals = new Dictionary<string, ManagedDatapack>(StringComparer.Ordinal); long total = 0;
        foreach (var file in journal.Files)
        {
            if (!state.Packs.TryGetValue(file.NewName, out var active) || active.Directory || active.Hash != file.Sha256) throw new IOException("更新后的数据包已改变。");
            if (file.OldName is not { } name) continue;
            var pack = await FingerprintManagedPackAsync(Path.Combine(directory, "payload", name), ct); total += pack.Guards.Where(g => !g.Final.Directory).Sum(g => g.Final.Size);
            if (total > 256L * 1024 * 1024) throw new IOException("恢复备份超过 256 MiB 预算。");
            if (pack.Hash != file.OldHash || pack.Directory != file.OldDirectory) throw new IOException("原数据包备份校验失败。"); originals.Add(name, pack);
        }
        var replaced = journal.Files.Select(f => f.NewName).ToHashSet(StringComparer.Ordinal); var untouched = state.Packs.Values.Where(p => !replaced.Contains(p.Name)).ToArray();
        if (untouched.Concat(originals.Values).Select(p => ContentInstallFiles.PathKey(p.Name)).Distinct().Count() != untouched.Length + originals.Count) throw new IOException("恢复目标存在大小写或 Unicode 重名。");
        if (DatapackWorkspaceRevision(before, mb, untouched.Concat(originals.Values)) != journal.OriginalRevision) throw new IOException("恢复前状态与原始修订不符，未猜测恢复范围。");
        foreach (var pack in originals.Values) foreach (var snapshot in pack.Guards) snapshot.Verify();
        if ((await ReadDatapackWorkspaceAsync(world, ct)).Revision != state.Revision) throw new IOException("核对恢复期间目标改变。");
        return new(Revision(receipt), state.Revision, directory, journal, before, after, mb, ma, originals);
    }
    private async Task CopyBackupPackAsync(ManagedDatapack source, string destination, CancellationToken ct)
    {
        foreach (var guard in source.Guards) guard.Verify(); var root = source.Guards[0].Path;
        if (source.Directory) EnsureTrashDirectory(destination);
        foreach (var guard in source.Guards)
        {
            ct.ThrowIfCancellationRequested(); var path = source.Directory ? Path.Combine(destination, Path.GetRelativePath(root, guard.Path)) : destination;
            if (guard.Final.Directory) EnsureTrashDirectory(path);
            else { var bytes = await ReadGuardedAsync(guard, limits.MaxPackBytes, ct); await SaveTrashFileAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path), bytes, ct); }
        }
        foreach (var guard in source.Guards) guard.Verify();
        if ((await FingerprintManagedPackAsync(destination, ct)).Hash != source.Hash) throw new IOException("恢复暂存副本校验失败。");
    }
    internal async Task<WorldDatapackRestoreReceipt> RestoreDatapackBackupAsync(string world, string id, string expectedWorkspace, string expectedReceipt, CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try
        {
            var plan = await PrepareDatapackRestoreAsync(world, id, ct);
            if (plan.WorkspaceRevision != expectedWorkspace || plan.Revision != expectedReceipt) throw new IOException("恢复预检已过期，请重新检查。");
            var key = Guid.NewGuid().ToString("N"); var stage = WorldChild(world, ".shou-datapack-restore-" + key); var preserved = Path.Combine(plan.Directory, "restore-current", key);
            EnsureTrashDirectory(stage); var stageGuard = Capture(stage); EnsureTrashDirectory(Path.Combine(preserved, "payload"));
            var displaced = new List<DatapackUpdateJournalFile>(); var published = new List<ManagedDatapack>();
            var levelPath = WorldChild(world, "level.dat"); var manifestPath = WorldChild(world, ContentInstallFiles.ManifestName);
            void MoveRecorded(string source, string target, bool directory, Action record)
            {
                var original = Capture(source);
                try { MovePack(source, target, directory); }
                finally { var result = Capture(target, true); if (result.Exists && result.Final.SameNode(original.Final)) record(); }
            }
            try
            {
                foreach (var pack in plan.Originals.Values) { stageGuard.Verify(); await CopyBackupPackAsync(pack, Path.Combine(stage, pack.Name), ct); }
                await SaveTrashFileAsync(preserved, "level.before", plan.LevelAfter, ct); if (plan.ManifestAfter is not null) await SaveTrashFileAsync(preserved, "manifest.before", plan.ManifestAfter, ct);
                await SaveTrashFileAsync(preserved, "receipt.json", JsonSerializer.SerializeToUtf8Bytes(new { Format = 1, BackupId = id, OriginalRevision = plan.WorkspaceRevision, RestoredRevision = plan.Journal.OriginalRevision }), ct);
                var current = await PrepareDatapackRestoreAsync(world, id, ct); if (current.Revision != plan.Revision || current.WorkspaceRevision != plan.WorkspaceRevision) throw new IOException("备份或目标在准备恢复期间改变。");
                foreach (var file in plan.Journal.Files)
                {
                    ct.ThrowIfCancellationRequested(); MoveRecorded(WorldChild(world, "datapacks/" + file.NewName), Path.Combine(preserved, "payload", file.NewName), false, () => displaced.Add(file));
                }
                foreach (var pack in plan.Originals.Values)
                {
                    ct.ThrowIfCancellationRequested(); stageGuard.Verify(); var target = WorldChild(world, "datapacks/" + pack.Name); RequireVacantPackPath(target);
                    MoveRecorded(Path.Combine(stage, pack.Name), target, pack.Directory, () => published.Add(pack));
                }
                foreach (var file in displaced) if ((await FingerprintManagedPackAsync(Path.Combine(preserved, "payload", file.NewName), ct)).Hash != file.Sha256) throw new IOException("替换版本保存核验失败。");
                if (!SameBytes(plan.LevelAfter, plan.LevelBefore)) await AtomicWriteAsync(levelPath, plan.LevelBefore, plan.LevelAfter, ct);
                if (!SameBytes(plan.ManifestAfter, plan.ManifestBefore)) await AtomicWriteAsync(manifestPath, plan.ManifestBefore!, plan.ManifestAfter, ct);
                if ((await ReadDatapackWorkspaceAsync(world, ct)).Revision != plan.Journal.OriginalRevision) throw new IOException("恢复后的文件或元数据核验失败。");
                ct.ThrowIfCancellationRequested();
                // This marker is the commit point. A later cancellation must not turn a completed restore into a rollback.
                await AtomicWriteAsync(Path.Combine(plan.Directory, "restored"), Encoding.UTF8.GetBytes(key), null, CancellationToken.None);
                return new(plan.Directory, preserved);
            }
            catch (Exception error)
            {
                try
                {
                    var marker = Capture(Path.Combine(plan.Directory, "restored"), true);
                    if (marker.Exists)
                    {
                        if (!SameBytes(await ReadGuardedAsync(marker, 128, CancellationToken.None), Encoding.UTF8.GetBytes(key))) throw new IOException("恢复完成标记被其它操作占用。");
                        marker.Verify(); File.Delete(marker.Path);
                    }
                    await RollbackMetadataAsync(manifestPath, plan.ManifestAfter, plan.ManifestBefore, 4 * 1024 * 1024);
                    await RollbackMetadataAsync(levelPath, plan.LevelAfter, plan.LevelBefore, nbtLimits.MaxInputBytes);
                    foreach (var pack in published.AsEnumerable().Reverse())
                    {
                        var source = WorldChild(world, "datapacks/" + pack.Name); if ((await FingerprintManagedPackAsync(source, CancellationToken.None)).Hash != pack.Hash) throw new IOException("恢复目标被其它程序改变。"); MovePack(source, Path.Combine(stage, pack.Name), pack.Directory);
                    }
                    foreach (var file in displaced.AsEnumerable().Reverse())
                    {
                        var target = WorldChild(world, "datapacks/" + file.NewName); RequireVacantPackPath(target); var source = Path.Combine(preserved, "payload", file.NewName);
                        if ((await FingerprintManagedPackAsync(source, CancellationToken.None)).Hash != file.Sha256) throw new IOException("替换版本已改变，未覆盖当前文件。"); MovePack(source, target, false);
                    }
                    await SaveTrashFileAsync(preserved, "rolled-back", [], CancellationToken.None);
                }
                catch (Exception rollback) { throw new IOException("恢复及自动回滚未完成；原备份和替换版本保留在更新备份目录，请先核对，勿覆盖现有文件。", new AggregateException(error, rollback)); }
                throw;
            }
            finally { stageGuard.Verify(); if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        finally { mutation.Release(); }
    }
}
