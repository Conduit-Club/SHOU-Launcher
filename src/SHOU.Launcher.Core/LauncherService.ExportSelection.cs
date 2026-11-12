using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceExportSelectionService
{
    public async Task<IReadOnlyList<InstanceExportEntry>> GetInstanceExportEntriesAsync(string instanceId, string directory = "", CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); var instance = ContentTarget(instanceId, false);
        var folder = directory.Length == 0 ? instance.GameDirectory : ContentInstallFiles.Literal(instance.GameDirectory, directory);
        SafePath.RejectLinks(folder); if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("导出目录已移动，请重新读取。");
        var rows = new List<InstanceExportEntry>();
        foreach (var child in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(instance.GameDirectory, child.FullName).Replace('\\', '/');
            var isDirectory = child is DirectoryInfo; var selectable = true; string? reason = null;
            try
            {
                PackPayloadPath.Validate(relative); _ = ContentInstallFiles.Literal(instance.GameDirectory, relative);
                if (!ShouPackArchive.Allowed(isDirectory ? relative + "/__scope" : relative)) { selectable = false; reason = "启动器凭据不包含在整合包内。"; }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException) { selectable = false; reason = "此路径无法作为导出内容。"; }
            rows.Add(new(relative, isDirectory, child is FileInfo file ? file.Length : 0, selectable, reason));
        }
        return rows.OrderByDescending(r => r.Directory).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
