using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Instances;

public sealed partial class InstanceWorldStore
{
    internal async Task<WorldDatapackUpdateReceipt> ApplyDatapackUpdateAsync(string worldDirectoryName, string expectedRevision,
        IReadOnlyList<DatapackReplacement> replacements, byte[]? nextManifest, string rootNewName, CancellationToken ct)
    {
        if (replacements.Count is < 1 or > 64 || replacements.Select(r => ContentInstallFiles.PathKey(r.NewName)).Distinct().Count() != replacements.Count || replacements.Where(r => r.OldName is not null).Select(r => r.OldName).Distinct().Count() != replacements.Count(r => r.OldName is not null)) throw new InvalidDataException("更新文件计划存在重复或超过 64 项。");
        await mutation.WaitAsync(ct);
        try
        {
            var state = await ReadDatapackWorkspaceAsync(worldDirectoryName, ct);
            if (state.Revision != expectedRevision) throw new IOException("存档、数据包或安装清单已改变，请重新检查更新。");
            var old = replacements.Where(r => r.OldName is not null).ToDictionary(r => r.OldName!, r => state.Packs.GetValueOrDefault(r.OldName!) ?? throw new IOException("原数据包缺失。"), StringComparer.Ordinal);
            foreach (var item in replacements)
            {
                _ = Leaf(item.NewName); if (!item.NewName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !ContentInstallFiles.Hash(item.Sha256, 64)) throw new InvalidDataException("更新文件须为已核验的 ZIP。");
                var conflict = state.Packs.Keys.FirstOrDefault(name => ContentInstallFiles.PathKey(name) == ContentInstallFiles.PathKey(item.NewName));
                if (conflict is not null && !old.ContainsKey(conflict)) throw new IOException("目标已有其它数据包，不覆盖：" + item.NewName);
            }
            var world = await ReadEditableWorldAsync(worldDirectoryName, ct); RequireWorldRevision(world, state.LevelRevision);
            var mapping = replacements.Where(r => r.OldName is not null).ToDictionary(r => "file/" + r.OldName, r => "file/" + r.NewName, StringComparer.Ordinal);
            var active = world.Enabled!.Select(id => mapping.GetValueOrDefault(id, id)).ToList(); var inactive = world.Disabled!.Select(id => mapping.GetValueOrDefault(id, id)).ToList();
            foreach (var item in replacements.Where(r => r.Enable))
            {
                var id = "file/" + item.NewName;
                if (inactive.Contains(id, StringComparer.Ordinal)) throw new InvalidOperationException("必需依赖被明确禁用，请先核对启用状态。");
                if (!active.Contains(id, StringComparer.Ordinal)) { var index = active.IndexOf("file/" + rootNewName); active.Insert(index < 0 ? active.Count : index, id); }
            }
            if (active.Distinct(StringComparer.Ordinal).Count() != active.Count || inactive.Distinct(StringComparer.Ordinal).Count() != inactive.Count || active.Intersect(inactive, StringComparer.Ordinal).Any()) throw new IOException("新文件名与存档已有数据包引用冲突。");
            var nextLevel = PackListsBytes(world, active, inactive, ct);
            var baseBackup = WorldChild(worldDirectoryName, ".shou-backups/datapack-updates");
            var existingBackups = Capture(baseBackup, true);
            if (existingBackups.Exists && Directory.EnumerateDirectories(baseBackup).Take(256).Count() >= 256) throw new IOException("更新备份已达 256 项，请先备份并整理。");
            var idKey = Guid.NewGuid().ToString("N"); var stage = WorldChild(worldDirectoryName, ".shou-datapack-update-" + idKey); var backup = Path.Combine(baseBackup, idKey);
            EnsureTrashDirectory(stage); var stageGuard = Capture(stage); EnsureTrashDirectory(backup); EnsureTrashDirectory(Path.Combine(backup, "payload"));
            var moved = new List<string>(); var published = new List<DatapackReplacement>();
            void MoveRecorded(string source, string destination, bool directory, Action record)
            {
                var before = Capture(source);
                try { MovePack(source, destination, directory); }
                finally
                {
                    var output = Capture(destination, true);
                    if (output.Exists && output.Final.SameNode(before.Final)) record();
                }
            }
            var levelPath = WorldChild(worldDirectoryName, "level.dat"); var manifestPath = WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName);
            async Task VerifyMetadata(CancellationToken token)
            {
                if (Revision(await ReadOptionalAsync(levelPath, nbtLimits.MaxInputBytes, token)) != state.LevelRevision || Revision(await ReadOptionalAsync(manifestPath, 4 * 1024 * 1024, token)) != state.ManifestRevision) throw new IOException("存档或安装清单在更新期间改变。");
            }
            try
            {
                foreach (var item in replacements)
                {
                    ct.ThrowIfCancellationRequested(); stageGuard.Verify();
                    using var source = PackArchiveReader.Open(item.Source); if (await source.Sha256Async(ct) != item.Sha256) throw new IOException("更新 ZIP 自预检后改变。");
                    var target = Path.Combine(stage, item.NewName); await source.CopyToAsync(target, ct);
                    _ = await WorldContentArchive.DatapackAsync(target, state.Format, ct);
                }
                await SaveTrashFileAsync(backup, "level.before", world.Bytes, ct);
                await SaveTrashFileAsync(backup, "level.after", nextLevel, ct);
                if (state.Manifest is not null) await SaveTrashFileAsync(backup, "manifest.before", state.Manifest, ct);
                if (nextManifest is not null) await SaveTrashFileAsync(backup, "manifest.after", nextManifest, ct);
                var resultingPacks = state.Packs.Values.Where(p => !old.ContainsKey(p.Name)).Concat(replacements.Select(r => new ManagedDatapack(r.NewName, false, r.Sha256, [])));
                var journal = new DatapackUpdateJournal(2, worldDirectoryName, DateTimeOffset.UtcNow, state.Revision,
                    DatapackWorkspaceRevision(nextLevel, nextManifest, resultingPacks), Revision(world.Bytes), Revision(nextLevel), Revision(state.Manifest), Revision(nextManifest),
                    replacements.Select(r => new DatapackUpdateJournalFile(r.OldName, r.NewName, r.Sha256, r.OldName is { } name ? old[name].Hash : null, r.OldName is { } previous && old[previous].Directory, r.Enable)).ToArray());
                var journalBytes = JsonSerializer.SerializeToUtf8Bytes(journal);
                if (journalBytes.Length > DatapackJournalLimit) throw new InvalidDataException("更新备份收据超过读取预算，请减少本次更新项目。");
                await SaveTrashFileAsync(backup, "receipt.json", journalBytes, ct);
                var latest = await ReadDatapackWorkspaceAsync(worldDirectoryName, ct); if (latest.Revision != expectedRevision) throw new IOException("数据包在准备期间改变，未替换原文件。");
                foreach (var (name, pack) in old)
                {
                    ct.ThrowIfCancellationRequested(); foreach (var guard in pack.Guards) guard.Verify();
                    var source = WorldChild(worldDirectoryName, "datapacks/" + name); var destination = Path.Combine(backup, "payload", name);
                    MoveRecorded(source, destination, pack.Directory, () => moved.Add(name));
                    SHOU.Launcher.Core.IO.FileTransactionCheckpoint.Reach("update-original-moved");
                }
                foreach (var item in replacements)
                {
                    ct.ThrowIfCancellationRequested(); var target = WorldChild(worldDirectoryName, "datapacks/" + item.NewName); RequireVacantPackPath(target);
                    var staged = Capture(Path.Combine(stage, item.NewName)); stageGuard.Verify();
                    MoveRecorded(staged.Path, target, false, () => published.Add(item));
                }
                foreach (var pack in state.Packs.Values.Where(p => !old.ContainsKey(p.Name))) foreach (var guard in pack.Guards) guard.Verify();
                foreach (var (name, pack) in old) if ((await FingerprintManagedPackAsync(Path.Combine(backup, "payload", name), ct)).Hash != pack.Hash) throw new IOException("原包备份核验失败。");
                foreach (var item in published) if ((await FingerprintManagedPackAsync(WorldChild(worldDirectoryName, "datapacks/" + item.NewName), ct)).Hash != item.Sha256) throw new IOException("新包发布核验失败。");
                await VerifyMetadata(ct);
                if (!SameBytes(world.Bytes, nextLevel)) await AtomicWriteAsync(levelPath, nextLevel, world.Bytes, ct);
                if (!SameBytes(state.Manifest, nextManifest)) await AtomicWriteAsync(manifestPath, nextManifest!, state.Manifest, ct);
                if ((await ReadDatapackWorkspaceAsync(worldDirectoryName, ct)).Revision != journal.AfterRevision) throw new IOException("更新发布后的文件或元数据发生变化。");
                ct.ThrowIfCancellationRequested(); await SaveTrashFileAsync(backup, "complete", [], ct);
                return new(Revision(nextLevel), backup);
            }
            catch (Exception error)
            {
                try
                {
                    await RollbackMetadataAsync(manifestPath, state.Manifest, nextManifest, 4 * 1024 * 1024);
                    await RollbackMetadataAsync(levelPath, world.Bytes, nextLevel, nbtLimits.MaxInputBytes);
                    foreach (var item in published.AsEnumerable().Reverse())
                    {
                        var path = WorldChild(worldDirectoryName, "datapacks/" + item.NewName);
                        if ((await FingerprintManagedPackAsync(path, CancellationToken.None)).Hash != item.Sha256) throw new IOException("新包已被其它程序改变，保留恢复资料。");
                        MovePack(path, Path.Combine(stage, item.NewName), false);
                    }
                    foreach (var name in moved.AsEnumerable().Reverse())
                    {
                        var destination = WorldChild(worldDirectoryName, "datapacks/" + name); RequireVacantPackPath(destination);
                        var source = Path.Combine(backup, "payload", name); if ((await FingerprintManagedPackAsync(source, CancellationToken.None)).Hash != old[name].Hash) throw new IOException("备份已被改变，未覆盖目标。");
                        MovePack(source, destination, old[name].Directory);
                    }
                    await SaveTrashFileAsync(backup, "rolled-back", [], CancellationToken.None);
                }
                catch (Exception rollback) { throw new IOException("更新及自动回滚未完成；原包、元数据备份和收据保留在存档 .shou-backups/datapack-updates 中，请先核对恢复。", new AggregateException(error, rollback)); }
                throw;
            }
            finally { stageGuard.Verify(); if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        finally { mutation.Release(); }
    }
}
