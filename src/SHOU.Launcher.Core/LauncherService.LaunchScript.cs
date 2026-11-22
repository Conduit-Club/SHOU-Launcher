using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILaunchScriptService
{
    public Task ExportLaunchScriptAsync(string instanceId, string executable, string destination, bool overwrite = false, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        var instance = FindInstance(instanceId);
        if (Path.GetFileName(executable) is not ("SHOU.Launcher.Desktop.exe" or "SHOU.Launcher.Desktop") || !File.Exists(executable)) throw new ArgumentException("请从完整的启动器程序导出脚本。");
        var extension = Snapshot.Platform.OS == LauncherOS.Windows ? ".cmd" : ".command";
        if (!Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("此平台的启动脚本扩展名应为 " + extension);
        if (instance.Origin is { } origin) _ = await ExistingInstanceReader.ResolveAsync(origin, instance.VersionId, InstanceIdentity.GameVersion(instance), ct);
        var content = LaunchScript.Build(Snapshot.Platform.OS, executable, Paths.DataDirectory, instanceId, Paths.IsIsolated);
        await LaunchScript.SaveAsync(destination, content, overwrite, Snapshot.Platform.OS == LauncherOS.MacOS, ct);
    }, cancellationToken);
}
