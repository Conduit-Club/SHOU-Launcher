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
    public static IReadOnlyList<InstanceContentEntry> List(GameInstance instance, string kind, CancellationToken ct = default)
    {
        var folder = Folder(instance.GameDirectory, kind);
        if (!System.IO.Directory.Exists(folder)) return [];
        var entries = new List<InstanceContentEntry>();
        var iconBytes = 0;
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderByDescending(i => i.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var directory = item is DirectoryInfo;
            if ((kind == "worlds") != directory) continue;
            if (!directory && !Accepts(kind, item.Name)) continue;
            var file = SafePath.Within(folder, item.Name);
            var relative = kind switch { "worlds" => "saves/", "shaders" => "shaderpacks/", _ => kind + "/" } + item.Name;
            var metadata = kind == "mods" ? ModArchiveMetadata.Read(file, iconBytes < 16 * 1024 * 1024) : null;
            var resource = kind is "resourcepacks" or "shaders" ? ResourceArchiveMetadata.Read(file, iconBytes < 16 * 1024 * 1024, ct) : null;
            iconBytes += metadata?.Icon?.Length ?? resource?.Icon?.Length ?? 0;
            entries.Add(new(item.Name, relative, file, kind, directory ? 0 : ((FileInfo)item).Length,
                item.LastWriteTimeUtc, !item.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !item.Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase), directory,
                metadata?.Name ?? resource?.Name, metadata?.Version, metadata?.Icon ?? resource?.Icon, metadata?.Id, metadata?.Description ?? resource?.Description,
                metadata?.Dependencies, metadata?.Embedded, metadata?.Error ?? resource?.Error, item.CreationTimeUtc));
        }
        return entries;
    }
    public static bool Accepts(string kind, string name)
    {
        var extension = Path.GetExtension(name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToLowerInvariant();
        return kind switch { "mods" => extension is ".jar" or ".litemod", "screenshots" => extension is ".png" or ".jpg" or ".jpeg",
            "resourcepacks" or "shaders" => extension == ".zip", "schematics" => extension is ".litematic" or ".schematic" or ".schem" or ".nbt", _ => false };
    }
    public static async Task ImportAsync(GameInstance instance, string kind, string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("导入文件须为绝对路径。");
        var input = new FileInfo(source);
        if (!input.Exists || input.LinkTarget is not null || !Accepts(kind, input.Name)) throw new InvalidDataException("此文件不能导入所选类别。");
        var name = kind == "mods" ? input.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? input.Name[..^9] : input.Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? input.Name[..^4] : input.Name : input.Name;
        if (kind == "mods" && (!InstanceIdentity.SupportsModDirectory(instance.Loader) || name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase) && instance.Loader != "liteloader")) throw new InvalidOperationException("此实例的加载器不支持该模组目录或文件类型，未导入。");
        var folder = Folder(instance.GameDirectory, kind); System.IO.Directory.CreateDirectory(folder);
        var target = SafePath.Within(folder, name);
        if (Path.GetFullPath(source).Equals(target, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        if (File.Exists(target) && kind != "mods") throw new IOException("同名文件已存在，原文件保留。");
        var sourceModified = input.LastWriteTimeUtc;
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
            input.Refresh();
            if (!input.Exists || input.Length != new FileInfo(temporary).Length || input.LastWriteTimeUtc != sourceModified) throw new IOException("导入源在复制期间发生变化，未安装。");
            _ = SafePath.Within(instance.GameDirectory, kind == "shaders" ? "shaderpacks/" + name : kind + "/" + name);
            if (kind == "mods" && File.Exists(target)) await ModFileManagement.ReplaceImportedAsync(instance, "mods/" + name, temporary, ct).ConfigureAwait(false);
            else File.Move(temporary, target, false);
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
        var oldSuffix = source.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
        var isEnabled = !source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !oldSuffix;
        if (isEnabled == enabled) return;
        var next = enabled ? relative[..^(oldSuffix ? 4 : 9)] : relative + ".disabled";
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
