using System.IO.Compression;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Works only in a selected instance, never follows links while enumerating or exporting.</summary>
public static class InstanceWorkspace
{
    public static string Folder(string gameDirectory, string kind) => SafePath.Within(gameDirectory, kind switch
    { "worlds" => "saves", "screenshots" => "screenshots", "mods" => "mods", "resourcepacks" => "resourcepacks",
      "shaders" => "shaderpacks", "schematics" => "schematics", _ => throw new ArgumentException("未知实例内容类别。", nameof(kind)) });
    public static IReadOnlyList<InstanceContentEntry> List(GameInstance instance, string kind)
    {
        var folder = Folder(instance.GameDirectory, kind);
        if (!System.IO.Directory.Exists(folder)) return [];
        var entries = new List<InstanceContentEntry>();
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderByDescending(i => i.LastWriteTimeUtc))
        {
            if (entries.Count >= 2000) break;
            if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var directory = item is DirectoryInfo;
            if ((kind == "worlds") != directory) continue;
            if (!directory && !Accepts(kind, item.Name)) continue;
            var file = SafePath.Within(folder, item.Name);
            var relative = kind switch { "worlds" => "saves/", "shaders" => "shaderpacks/", _ => kind + "/" } + item.Name;
            entries.Add(new(item.Name, relative, file, kind, directory ? 0 : ((FileInfo)item).Length,
                item.LastWriteTimeUtc, !item.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase), directory));
        }
        return entries;
    }
    public static bool Accepts(string kind, string name)
    {
        var extension = Path.GetExtension(name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name).ToLowerInvariant();
        return kind switch { "mods" => extension == ".jar", "screenshots" => extension is ".png" or ".jpg" or ".jpeg",
            "resourcepacks" or "shaders" => extension == ".zip", "schematics" => extension is ".litematic" or ".schematic" or ".schem" or ".nbt", _ => false };
    }
    public static async Task ImportAsync(GameInstance instance, string kind, string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("导入文件须为绝对路径。");
        var input = new FileInfo(source);
        if (!input.Exists || input.LinkTarget is not null || !Accepts(kind, input.Name)) throw new InvalidDataException("此文件不能导入所选类别。");
        if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("单个导入文件超过512 MiB限制。");
        if (kind == "mods" && instance.Loader == "vanilla") throw new InvalidOperationException("原版实例无法加载模组，请先选择已安装加载器的实例。");
        var folder = Folder(instance.GameDirectory, kind); System.IO.Directory.CreateDirectory(folder);
        var target = SafePath.Within(folder, input.Name);
        if (File.Exists(target)) throw new IOException("同名文件已存在，原文件保留。");
        var temporary = SafePath.Within(folder, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                if (from.Length != input.Length) throw new IOException("导入文件在复制前发生变化。");
                await from.CopyToAsync(to, ct).ConfigureAwait(false);
                if (to.Length != input.Length) throw new IOException("导入文件复制不完整。");
            }
            _ = SafePath.Within(instance.GameDirectory, kind == "shaders" ? "shaderpacks/" + input.Name : kind + "/" + input.Name);
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void ToggleMod(GameInstance instance, string relative, bool enabled)
    {
        if (!relative.StartsWith("mods/", StringComparison.Ordinal) || relative[5..].Contains('/') || relative.Contains('\\') || !Accepts("mods", relative))
            throw new IOException("模组路径无效。");
        var source = SafePath.Within(instance.GameDirectory, relative);
        var file = new FileInfo(source);
        if (!file.Exists || file.LinkTarget is not null) throw new IOException("模组文件不存在或为链接。");
        var isEnabled = !source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (isEnabled == enabled) return;
        var next = enabled ? relative[..^9] : relative + ".disabled";
        var target = SafePath.Within(instance.GameDirectory, next);
        File.Move(source, target, false);
    }
    public static async Task<string> ExportAsync(GameInstance instance, string outputDirectory, bool includeWorlds, CancellationToken ct)
    {
        var root = SafePath.Canonical(instance.GameDirectory);
        var files = new List<(string Full, string Relative, long Size)>();
        foreach (var folderName in new[] { "mods", "resourcepacks", "shaderpacks", "config", "schematics", "saves" })
        {
            if (folderName == "saves" && !includeWorlds) continue;
            var folder = SafePath.Within(root, folderName);
            if (!System.IO.Directory.Exists(folder)) continue;
            void Visit(string current, string prefix, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (depth > 24) throw new IOException("实例文件夹嵌套过深。");
                foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var relative = prefix + "/" + item.Name;
                    var safe = SafePath.Within(root, relative);
                    if (item is DirectoryInfo) Visit(safe, relative, depth + 1);
                    else files.Add((safe, relative, ((FileInfo)item).Length));
                    if (files.Count > 20000) throw new IOException("导出文件数量超过限制。");
                }
            }
            Visit(folder, folderName, 0);
        }
        foreach (var name in new[] { "options.txt", "servers.dat" })
        {
            var path = SafePath.Within(root, name); var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is null) files.Add((path, name, info.Length));
        }
        var total = files.Sum(f => f.Size);
        if (total > 2L * 1024 * 1024 * 1024) throw new IOException("导出内容超过2 GiB，请减少导出范围。");
        System.IO.Directory.CreateDirectory(outputDirectory);
        var destination = SafePath.Within(outputDirectory, "SHOU-" + SafePath.Identifier(instance.Id) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
        var temp = SafePath.Within(outputDirectory, ".export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
                foreach (var item in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.Equals(SafePath.Within(root, item.Relative), item.Full, StringComparison.Ordinal) || new FileInfo(item.Full).LinkTarget is not null)
                        throw new IOException("导出期间实例路径发生变化。");
                    var entry = zip.CreateEntry(item.Relative, CompressionLevel.Fastest);
                    await using var output = entry.Open();
                    await using var input = new FileStream(item.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (input.Length != item.Size) throw new IOException("导出期间文件发生变化。");
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }
                var manifest = zip.CreateEntry("shou-instance.json");
                await using var metadata = manifest.Open();
                await JsonSerializer.SerializeAsync(metadata, new { schemaVersion = 1, instance.Name, instance.VersionId,
                    instance.Loader, instance.LoaderVersion, instance.MemoryMiB, exportedAt = DateTimeOffset.UtcNow }, cancellationToken: ct).ConfigureAwait(false);
            }
            File.Move(temp, destination, false); return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
