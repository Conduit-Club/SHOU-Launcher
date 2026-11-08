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
        return kind switch { "mods" => extension is ".jar" or ".litemod", "screenshots" => extension is ".png" or ".jpg" or ".jpeg",
            "resourcepacks" or "shaders" => extension == ".zip", "schematics" => extension is ".litematic" or ".schematic" or ".schem" or ".nbt", _ => false };
    }
    public static async Task ImportAsync(GameInstance instance, string kind, string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("导入文件须为绝对路径。");
        var input = new FileInfo(source);
        if (!input.Exists || input.LinkTarget is not null || !Accepts(kind, input.Name)) throw new InvalidDataException("此文件不能导入所选类别。");
        if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("单个导入文件超过512 MiB限制。");
        if (kind == "mods" && (!InstanceIdentity.SupportsModDirectory(instance.Loader) || input.Name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase) && instance.Loader != "liteloader")) throw new InvalidOperationException("此实例的加载器不支持该模组目录或文件类型，未导入。");
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
    public static Task<string> ExportAsync(GameInstance instance, string outputDirectory, bool includeWorlds, CancellationToken ct)
        => Content.ShouPackArchive.ExportAsync(instance, outputDirectory, includeWorlds, ct);
    private static void RejectLiteralLink(string path, bool directory)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("导出不跟随实例中的链接：" + info.Name);
    }
}
