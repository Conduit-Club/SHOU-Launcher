using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private GameInstance RebindDirectory(GameInstance instance, InstanceDirectories directories, LauncherSettings settings)
    {
        var expected = InstanceDirectoryPolicy.Describe(instance, Paths);
        if (!PathEquals(directories.IsolatedDirectory, expected.IsolatedDirectory) || !PathEquals(directories.SharedDirectory, expected.SharedDirectory)) throw new InvalidDataException("实例目录来源不一致，请重新接入原目录。");
        var path = InstanceDirectoryPolicy.Resolve(instance, directories, settings);
        if (PathEquals(path, instance.GameDirectory)) return instance with { Directories = directories };
        EnsureInstanceNotRunning(instance);
        Downloads.VerifiedDownloader.RejectLink(path);
        if (File.Exists(path)) throw new IOException("目标位置是文件，不能切换为游戏目录。");
        if (instance.Origin is null) EnsureIsolatedGamePath(path);
        else if (!PathEquals(path, instance.Origin.MinecraftRoot) && !PathEquals(path, instance.Origin.VersionDirectory)) throw new InvalidDataException("目标不属于已确认的实例来源。");
        var next = instance with { GameDirectory = path, Directories = directories, Installed = false };
        EnsureInstanceNotRunning(next);
        Downloads.VerifiedDownloader.RejectLink(Path.Combine(path, "mods"));
        // Do not mix two incompatible mod profiles in an already populated shared directory.
        if (Directory.Exists(Path.Combine(path, "mods")) && Directory.EnumerateFileSystemEntries(Path.Combine(path, "mods")).Any() && Snapshot.Instances.Any(i => i.Id != instance.Id && PathEquals(i.GameDirectory, path) && (InstanceIdentity.GameVersion(i) != InstanceIdentity.GameVersion(instance) || i.Loader != instance.Loader)))
            throw new IOException("共用目录已有其它版本或加载器的模组，请选择隔离目录。");
        return next;
    }
}
