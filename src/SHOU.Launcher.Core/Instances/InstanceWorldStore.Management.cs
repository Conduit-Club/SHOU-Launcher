using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Instances;

internal sealed record ManagedDatapack(string Name, bool Directory, string Hash, IReadOnlyList<WorldPathSnapshot> Guards);
internal sealed record DatapackWorkspace(string Revision, string LevelRevision, string ManifestRevision, byte[]? Manifest,
    string Version, int DataVersion, DataPackFormat Format, IReadOnlyList<string> Enabled, IReadOnlyList<string> Disabled,
    IReadOnlyDictionary<string, ManagedDatapack> Packs);
internal sealed record DatapackReplacement(string? OldName, string NewName, string Source, string Sha256, bool Enable = false);

public sealed partial class InstanceWorldStore
{
    private static DataPackFormat ManagementFormat(EditableWorld world) => (world.Version, world.DataVersion) switch
    {
        ("1.21.1", 3955) => new(48, 0), ("26.2", 4903) => new(107, 1),
        _ => throw new NotSupportedException("数据包管理写入目前验证 Java 1.21.1 和 26.2；其它世界版本保持只读。")
    };
    private async Task<ManagedDatapack> FingerprintManagedPackAsync(string path, CancellationToken ct)
    {
        var root = Capture(path); var guards = new List<WorldPathSnapshot> { root };
        if (!root.Final.Directory) return new(Path.GetFileName(path), false, Revision(await ReadGuardedAsync(root, limits.MaxPackBytes, ct)), guards);
        var files = new List<string>(); var pending = new Stack<(string Path, int Depth)>(); pending.Push((path, 0)); var nodes = 0;
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested(); if (current.Depth > 24) throw new IOException("数据包目录过深。");
            foreach (var child in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (++nodes > 5000) throw new IOException("数据包目录超过 5000 项预算。"); var guard = Capture(child); guards.Add(guard);
                if (guard.Final.Directory) pending.Push((child, current.Depth + 1)); else files.Add(child);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long total = 0;
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var bytes = await ReadGuardedAsync(Capture(file), limits.MaxPackBytes, ct); total += bytes.Length;
            if (total > 256L * 1024 * 1024) throw new IOException("数据包目录超过 256 MiB 预算。");
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/') + "\0")); hash.AppendData(SHA256.HashData(bytes));
        }
        foreach (var guard in guards) guard.Verify(); return new(Path.GetFileName(path), true, Convert.ToHexStringLower(hash.GetHashAndReset()), guards);
    }
    internal async Task<DatapackWorkspace> ReadDatapackWorkspaceAsync(string worldDirectoryName, CancellationToken ct)
    {
        var world = await ReadEditableWorldAsync(worldDirectoryName, ct); var format = ManagementFormat(world);
        if (world.Enabled is null || world.Disabled is null) throw new InvalidDataException("存档缺少明确的数据包启停列表，未猜测状态。");
        var manifest = await ReadOptionalAsync(WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName), 4 * 1024 * 1024, ct);
        var packs = new Dictionary<string, ManagedDatapack>(StringComparer.Ordinal); var names = new HashSet<string>(); long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(SHA256.HashData(world.Bytes));
        if (manifest is not null) hash.AppendData(SHA256.HashData(manifest));
        foreach (var file in await GetDatapacksAsync(worldDirectoryName, ct))
        {
            if (!names.Add(ContentInstallFiles.PathKey(file.Name))) throw new IOException("已有数据包存在大小写或 Unicode 重名。");
            var pack = await FingerprintManagedPackAsync(WorldChild(worldDirectoryName, "datapacks/" + Leaf(file.Name)), ct);
            total += pack.Guards.Where(g => !g.Final.Directory).Sum(g => g.Final.Size);
            if (total > 256L * 1024 * 1024) throw new IOException("已有数据包总读取超过 256 MiB。");
            packs.Add(pack.Name, pack); hash.AppendData(Encoding.UTF8.GetBytes(pack.Name + "\0" + pack.Hash));
        }
        foreach (var pack in packs.Values) foreach (var guard in pack.Guards) guard.Verify();
        if (Revision(await ReadOptionalAsync(WorldChild(worldDirectoryName, "level.dat"), nbtLimits.MaxInputBytes, ct)) != Revision(world.Bytes) || Revision(await ReadOptionalAsync(WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName), 4 * 1024 * 1024, ct)) != Revision(manifest)) throw new IOException("存档或安装清单在读取期间改变。");
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), Revision(world.Bytes), Revision(manifest), manifest, world.Version!, world.DataVersion!.Value, format, world.Enabled, world.Disabled, packs);
    }
    private async Task<InspectedDatapack> InspectManagedPackAsync(string world, ManagedDatapack pack, DataPackFormat format, CancellationToken ct)
    {
        var path = WorldChild(world, "datapacks/" + Leaf(pack.Name));
        return pack.Directory ? await WorldContentArchive.FolderDatapackAsync(path, format, ct) : await WorldContentArchive.DatapackAsync(path, format, ct);
    }
    public async Task<WorldDatapackOrderState> GetDatapackOrderAsync(string worldDirectoryName, CancellationToken ct = default)
    {
        var state = await ReadDatapackWorkspaceAsync(worldDirectoryName, ct); var order = new List<WorldDatapackOrderItem>();
        var resources = new Dictionary<string, string>(StringComparer.Ordinal); var warnings = new List<string>(); long expanded = 0;
        foreach (var id in state.Enabled.Where(id => id.StartsWith("file/", StringComparison.Ordinal)))
        {
            if (!state.Packs.TryGetValue(Leaf(id[5..]), out var pack)) throw new IOException("已启用数据包文件缺失，请先核对存档。");
            var inspected = await InspectManagedPackAsync(worldDirectoryName, pack, state.Format, ct); expanded += inspected.ExpandedBytes;
            if (expanded > 256L * 1024 * 1024 || resources.Count > 50000) throw new IOException("数据包资源超过扫描预算。");
            foreach (var path in inspected.Resources.Keys)
            {
                if (resources.TryGetValue(path, out var other) && warnings.Count < 10) warnings.Add(other + " / " + pack.Name + "：" + path);
                resources[path] = pack.Name;
            }
            order.Add(new(id, pack.Name, pack.Hash));
        }
        foreach (var pack in state.Packs.Values) foreach (var guard in pack.Guards) guard.Verify();
        return new(state.Revision, order, warnings.Distinct().ToArray());
    }
    public async Task<WorldEditReceipt> SaveDatapackOrderAsync(string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackOrderItem> order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order); if (order.Count > 1024) throw new InvalidDataException("数据包顺序超过预算。");
        await mutation.WaitAsync(ct);
        try
        {
            var state = await ReadDatapackWorkspaceAsync(worldDirectoryName, ct);
            if (state.Revision != expectedRevision) throw new IOException("存档或数据包已改变，请保留草稿并重新核对。");
            var currentOrder = await GetDatapackOrderAsync(worldDirectoryName, ct);
            if (currentOrder.Revision != state.Revision) throw new IOException("数据包在核对期间改变。");
            var existing = currentOrder.Items;
            if (order.Count != existing.Count || order.Select(p => p.PackId).Distinct(StringComparer.Ordinal).Count() != order.Count || order.Any(p => !existing.Contains(p))) throw new InvalidDataException("排序只能重排完整的已启用本地数据包，不能添加、移除或替换内容。");
            var next = new Queue<string>(order.Select(p => p.PackId)); var active = state.Enabled.Select(id => id.StartsWith("file/", StringComparison.Ordinal) ? next.Dequeue() : id).ToList();
            if (active.SequenceEqual(state.Enabled)) return new(state.LevelRevision, null);
            var world = await ReadEditableWorldAsync(worldDirectoryName, ct); RequireWorldRevision(world, state.LevelRevision);
            var output = PackListsBytes(world, active, world.Disabled!, ct); var path = WorldChild(worldDirectoryName, "level.dat");
            try
            {
                foreach (var pack in state.Packs.Values) foreach (var guard in pack.Guards) guard.Verify();
                if (Revision(await ReadOptionalAsync(WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName), 4 * 1024 * 1024, ct)) != state.ManifestRevision) throw new IOException("安装清单在保存期间改变。");
                var backup = await AtomicWriteAsync(path, output, world.Bytes, ct);
                foreach (var pack in state.Packs.Values) foreach (var guard in pack.Guards) guard.Verify();
                if (Revision(await ReadOptionalAsync(WorldChild(worldDirectoryName, ContentInstallFiles.ManifestName), 4 * 1024 * 1024, ct)) != state.ManifestRevision) throw new IOException("安装清单在保存期间改变。");
                return new(Revision(output), backup);
            }
            catch { await RollbackMetadataAsync(path, world.Bytes, output, nbtLimits.MaxInputBytes); throw; }
        }
        finally { mutation.Release(); }
    }
    private byte[] PackListsBytes(EditableWorld world, List<string> enabled, List<string> disabled, CancellationToken ct)
    {
        var lists = world.Data.Find("DataPacks")!.Children.ToList(); Replace(lists, StringList("Enabled", enabled)); Replace(lists, StringList("Disabled", disabled));
        var data = world.Data.Children.ToList(); Replace(data, world.Data.Find("DataPacks")! with { Value = lists });
        var root = world.Document.Root.Children.ToList(); Replace(root, world.Data with { Value = data });
        return BoundedNbt.Write(world.Document with { Root = world.Document.Root with { Value = root } }, nbtLimits, ct);
    }
}
