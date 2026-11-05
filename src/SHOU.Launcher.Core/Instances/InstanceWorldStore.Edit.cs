using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Instances;

public sealed partial class InstanceWorldStore
{
    private const int KnownWorldVersion = 3955, KnownPackFormat = 48, MaximumPackMetadata = 65536;
    private const string KnownGameVersion = "1.21.1";
    private const string SupportedEditCapability = "仅当前隔离实例且游戏停止时可保存；已验证 Java 1.21.1，保存前自动备份 level.dat。";
    private sealed record EditableWorld(byte[] Bytes, NbtDocument Document, NbtTag Data, WorldMetadataValues Values,
        int? DataVersion, string? Version, List<string>? Enabled, List<string>? Disabled, bool Hardcore)
    {
        public bool KnownVersion => DataVersion == KnownWorldVersion && Version == KnownGameVersion;
        public bool CanEdit => KnownVersion && Values.AllowCommands is not null && Values.Difficulty is not null && Values.DifficultyLocked is not null;
    }
    public async Task<WorldEditState> GetEditStateAsync(string worldDirectoryName, CancellationToken ct = default)
    {
        var world = await ReadEditableWorldAsync(worldDirectoryName, ct); var files = await GetDatapacksAsync(worldDirectoryName, ct);
        var canToggle = world.KnownVersion && world.Enabled is not null && world.Disabled is not null;
        var packs = new List<WorldDatapackState>(); var budget = new PackReadBudget();
        foreach (var file in files)
        {
            var id = "file/" + file.Name; bool? enabled = world.Enabled?.Contains(id, StringComparer.Ordinal) == true ? true : world.Disabled?.Contains(id, StringComparer.Ordinal) == true ? false : null;
            try
            {
                var info = await InspectPackAsync(worldDirectoryName, file, budget, ct);
                packs.Add(new(file, id, info.Revision, enabled, info.Description, info.Format,
                    canToggle && info.ToggleSafe, !canToggle ? "此世界的版本或 DataPacks 状态尚未验证。" : info.Capability));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { packs.Add(new(file, id, "", enabled, "", null, false, "无法安全读取此数据包（" + ex.GetType().Name + "）；启停已禁用。")); }
        }
        return new(Revision(world.Bytes), world.DataVersion, world.Version, world.Values, packs, world.CanEdit, canToggle,
            !world.KnownVersion ? "存档写入仅验证 Java 1.21.1（3955）；其他版本只读。" : !world.CanEdit ? "存档设置字段不完整，设置保存已禁用；原文件保留。" : SupportedEditCapability);
    }
    public async Task<WorldEditReceipt> SaveMetadataAsync(string worldDirectoryName, string expectedRevision, WorldMetadataEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Difficulty is < 0 or > 3) throw new InvalidDataException("游戏难度须在 0 至 3 之间。");
        await mutation.WaitAsync(ct);
        try
        {
            var world = await ReadEditableWorldAsync(worldDirectoryName, ct); RequireWorldRevision(world, expectedRevision);
            if (!world.CanEdit) throw new InvalidOperationException("此世界的版本或设置字段尚未验证，不能保存。");
            if (world.Hardcore && edit.Difficulty != 3) throw new InvalidOperationException("极限模式的难度必须保持困难。");
            if (world.Values.DifficultyLocked == true && edit.DifficultyLocked && edit.Difficulty != world.Values.Difficulty) throw new InvalidOperationException("请先明确解除难度锁定，再修改难度。");
            if (world.Values.AllowCommands == edit.AllowCommands && world.Values.Difficulty == edit.Difficulty && world.Values.DifficultyLocked == edit.DifficultyLocked) return new(expectedRevision, null);
            var data = world.Data.Children.ToList(); Replace(data, new(NbtType.Byte, "allowCommands", (sbyte)(edit.AllowCommands ? 1 : 0)));
            Replace(data, new(NbtType.Byte, "Difficulty", (sbyte)edit.Difficulty)); Replace(data, new(NbtType.Byte, "DifficultyLocked", (sbyte)(edit.DifficultyLocked ? 1 : 0)));
            return await CommitWorldDataAsync(worldDirectoryName, world, data, ct);
        }
        finally { mutation.Release(); }
    }
    public async Task<WorldEditReceipt> SetDatapacksEnabledAsync(string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackSelection> selected, bool enabled, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count is < 1 or > 64 || selected.Select(p => p.PackId).Distinct(StringComparer.Ordinal).Count() != selected.Count) throw new InvalidDataException("请选择 1 至 64 个不同的数据包。");
        await mutation.WaitAsync(ct);
        try
        {
            var world = await ReadEditableWorldAsync(worldDirectoryName, ct); RequireWorldRevision(world, expectedRevision);
            if (!world.KnownVersion || world.Enabled is null || world.Disabled is null) throw new InvalidOperationException("此世界的 DataPacks 状态或版本尚未验证，不能写入。");
            var files = await GetDatapacksAsync(worldDirectoryName, ct); var budget = new PackReadBudget(); var guards = new List<WorldPathSnapshot>();
            foreach (var selection in selected)
            {
                if (selection.PackId.Length > 1024 || !selection.PackId.StartsWith("file/", StringComparison.Ordinal)) throw new InvalidDataException("只能更改明确选择的本地文件数据包；内建包不能修改。");
                var leaf = Leaf(selection.PackId[5..]); var file = files.SingleOrDefault(f => f.Name == leaf) ?? throw new IOException("所选数据包文件已经改变或缺失。");
                var info = await InspectPackAsync(worldDirectoryName, file, budget, ct);
                if (!info.ToggleSafe) throw new InvalidOperationException(info.Capability);
                if (selection.ContentRevision != info.Revision) throw new IOException("数据包内容已改变，请重新读取后再选择。");
                guards.AddRange(info.Guards);
            }
            var active = world.Enabled.ToList(); var inactive = world.Disabled.ToList(); var beforeActive = active.ToArray(); var beforeInactive = inactive.ToArray();
            foreach (var selection in selected)
            { var target = enabled ? active : inactive; var other = enabled ? inactive : active; other.Remove(selection.PackId); if (!target.Contains(selection.PackId, StringComparer.Ordinal)) target.Add(selection.PackId); }
            if (active.SequenceEqual(beforeActive) && inactive.SequenceEqual(beforeInactive)) return new(expectedRevision, null);
            var packs = world.Data.Find("DataPacks")!.Children.ToList(); Replace(packs, StringList("Enabled", active)); Replace(packs, StringList("Disabled", inactive));
            var data = world.Data.Children.ToList(); Replace(data, world.Data.Find("DataPacks")! with { Value = packs });
            foreach (var guard in guards) guard.Verify();
            var receipt = await CommitWorldDataAsync(worldDirectoryName, world, data, ct);
            foreach (var guard in guards) guard.Verify();
            return receipt;
        }
        finally { mutation.Release(); }
    }
    private async Task<EditableWorld> ReadEditableWorldAsync(string world, CancellationToken ct)
    {
        var bytes = await ReadOptionalAsync(WorldChild(world, "level.dat"), nbtLimits.MaxInputBytes, ct) ?? throw new InvalidDataException("世界缺少 level.dat。");
        var document = BoundedNbt.Read(bytes, nbtLimits, ct); var data = document.Root.Find("Data");
        if (data is not { Type: NbtType.Compound }) throw new InvalidDataException("世界缺少 Java Data compound。");
        var version = data.Find("Version"); if (version is not null && version.Type != NbtType.Compound) throw new InvalidDataException("世界版本字段类型无效。");
        var difficulty = ReadByte(data.Find("Difficulty")); if (difficulty is < 0 or > 3) throw new InvalidDataException("世界难度字段无效。");
        var values = new WorldMetadataValues(ReadBoolean(data.Find("allowCommands")), difficulty, ReadBoolean(data.Find("DifficultyLocked")),
            ReadInt(data.Find("SpawnX")), ReadInt(data.Find("SpawnY")), ReadInt(data.Find("SpawnZ")), ReadLong(data.Find("Time")));
        var packs = data.Find("DataPacks"); if (packs is not null && packs.Type != NbtType.Compound) throw new InvalidDataException("世界 DataPacks 字段类型无效。");
        var enabled = ReadPackList(packs?.Find("Enabled")); var disabled = ReadPackList(packs?.Find("Disabled"));
        if (enabled is not null && disabled is not null && enabled.Intersect(disabled, StringComparer.Ordinal).Any()) throw new InvalidDataException("同一数据包同时处于启用和禁用列表，原文件保留。");
        return new(bytes, document, data, values, ReadInt(data.Find("DataVersion")), ReadString(version?.Find("Name")), enabled, disabled, ReadBoolean(data.Find("hardcore")) == true);
    }
    private static int? ReadByte(NbtTag? tag) => tag is null ? null : tag.Type == NbtType.Byte ? (sbyte)tag.Value : throw new InvalidDataException("世界设置字段须为 Byte。");
    private static bool? ReadBoolean(NbtTag? tag) => ReadByte(tag) switch { null => null, 0 => false, 1 => true, _ => throw new InvalidDataException("世界布尔字段须为 0 或 1。") };
    private List<string>? ReadPackList(NbtTag? tag)
    {
        if (tag is null) return null;
        if (tag is not { Type: NbtType.List, Value: NbtList list } || list.ElementType != NbtType.String && !(list.Items.Count == 0 && list.ElementType == NbtType.End) || list.Items.Count > limits.MaxDatapacks) throw new InvalidDataException("世界数据包状态列表无效。");
        var result = list.Items.Select(item => ReadString(item) ?? throw new InvalidDataException("数据包标识无效。" )).ToList();
        if (result.Any(id => id.Length is < 1 or > 1024 || id.Any(char.IsControl)) || result.Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("数据包标识重复或超过边界。");
        return result;
    }
    private static NbtTag StringList(string name, List<string> values) => new(NbtType.List, name, new NbtList(NbtType.String, values.Select(value => new NbtTag(NbtType.String, "", value)).ToArray()));
    private static void RequireWorldRevision(EditableWorld world, string expected)
    { if (Revision(world.Bytes) != expected) throw new IOException("level.dat 已改变，请保留草稿并重新读取后核对。"); }
    private async Task<WorldEditReceipt> CommitWorldDataAsync(string worldDirectory, EditableWorld original, List<NbtTag> values, CancellationToken ct)
    {
        var root = original.Document.Root.Children.ToList(); Replace(root, original.Data with { Value = values });
        var output = BoundedNbt.Write(original.Document with { Root = original.Document.Root with { Value = root } }, nbtLimits, ct);
        var backup = await AtomicWriteAsync(WorldChild(worldDirectory, "level.dat"), output, original.Bytes, ct); return new(Revision(output), backup);
    }
    private sealed class PackReadBudget { public long Bytes, Expanded; public int Entries; }
    private sealed record PackInspection(string Revision, string Description, int? Format, bool ToggleSafe, string Capability, IReadOnlyList<WorldPathSnapshot> Guards);
    private async Task<PackInspection> InspectPackAsync(string world, WorldDatapackFile file, PackReadBudget budget, CancellationToken ct)
    {
        var path = WorldChild(world, "datapacks/" + Leaf(file.Name)); var guard = Capture(path); var guards = new List<WorldPathSnapshot> { guard }; byte[] metadata; string revision;
        if (file.IsDirectory)
        {
            var identities = new List<string>(); var pending = new Stack<(string Path, int Depth)>(); pending.Push((path, 0)); long total = 0;
            while (pending.TryPop(out var current))
            {
                if (current.Depth > 32) throw new InvalidDataException("数据包目录过深。"); ct.ThrowIfCancellationRequested();
                var directory = Capture(current.Path); guards.Add(directory);
                foreach (var item in Directory.EnumerateFileSystemEntries(current.Path).Take(limits.MaxPackEntries - budget.Entries + 1).Order(StringComparer.Ordinal))
                {
                    if (++budget.Entries > limits.MaxPackEntries) throw new InvalidDataException("数据包文件数量超过读取预算。");
                    var node = Capture(item); guards.Add(node); identities.Add(item + ":" + node.Final);
                    if (node.Final.Directory) pending.Push((item, current.Depth + 1));
                    else if ((total += node.Final.Size) > limits.MaxPackExpandedBytes || (budget.Expanded += node.Final.Size) > 512L * 1024 * 1024) throw new InvalidDataException("数据包目录超过读取预算。");
                }
                directory.Verify();
            }
            var meta = Capture(Path.Combine(path, "pack.mcmeta")); guards.Add(meta); metadata = await ReadGuardedAsync(meta, MaximumPackMetadata, ct);
            revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identities) + "\n" + Convert.ToHexString(SHA256.HashData(metadata))))).ToLowerInvariant();
        }
        else
        {
            if ((budget.Bytes += guard.Final.Size) > 128L * 1024 * 1024) throw new InvalidDataException("数据包 ZIP 总读取预算已用尽。");
            var bytes = await ReadGuardedAsync(guard, limits.MaxPackBytes, ct); ValidateZipDirectoryBudget(bytes);
            using var memory = new MemoryStream(bytes, false); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries) if (++budget.Entries > limits.MaxPackEntries || (budget.Expanded += entry.Length) > 512L * 1024 * 1024) throw new InvalidDataException("数据包 ZIP 总解压预算已用尽。");
            await ValidateDatapackAsync(bytes, ct);
            var meta = zip.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("数据包缺少根 pack.mcmeta。");
            if (meta.Length > MaximumPackMetadata) throw new InvalidDataException("数据包元数据超过上限。");
            metadata = new byte[checked((int)meta.Length)]; using var stream = meta.Open(); await stream.ReadExactlyAsync(metadata, ct); revision = Revision(bytes);
        }
        foreach (var item in guards) item.Verify();
        _ = new UTF8Encoding(false, true).GetCharCount(metadata);
        using var json = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object) throw new InvalidDataException("数据包元数据缺少 pack 对象。");
        var format = pack.TryGetProperty("pack_format", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : (int?)null;
        var description = pack.TryGetProperty("description", out var text) ? Description(text, 0) : "";
        var featurePack = json.RootElement.TryGetProperty("features", out _); var safe = format == KnownPackFormat && !featurePack;
        return new(revision, description, format, safe, featurePack ? "包含实验特性声明，不可在启动器中更改启停。" : format != KnownPackFormat ? "仅验证数据包格式 48；此数据包启停已禁用。" : "已核实本地文件与格式；启停在下次进入世界时生效。", guards);
    }
    private static string Description(JsonElement element, int depth)
    {
        if (depth > 8) return "";
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Take(64).Select(item => Description(item, depth + 1))),
            JsonValueKind.Object => (element.TryGetProperty("text", out var textValue) ? Description(textValue, depth + 1) : element.TryGetProperty("translate", out var translated) && translated.ValueKind == JsonValueKind.String ? translated.GetString() ?? "" : "") + (element.TryGetProperty("extra", out var extra) ? Description(extra, depth + 1) : ""),
            _ => ""
        };
        return new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(2048).ToArray());
    }
}
