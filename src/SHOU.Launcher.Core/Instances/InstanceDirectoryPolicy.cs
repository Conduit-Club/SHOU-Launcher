using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Instances;

public static class InstanceDirectoryPolicy
{
    public static InstanceDirectories Describe(GameInstance instance, IAppPaths paths)
    {
        if (instance.Directories is { } stored) return stored;
        if (instance.Origin is { } origin) return new(InstanceDirectoryMode.Current, origin.VersionDirectory, origin.MinecraftRoot);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.GameDirectory));
        var parent = Path.GetDirectoryName(current) ?? paths.InstancesDirectory;
        return new(InstanceDirectoryMode.Current, Path.GetFileName(current) == "shared-game" ? Path.Combine(parent, instance.Id) : current, Path.Combine(parent, "shared-game"));
    }
    public static string Resolve(GameInstance instance, InstanceDirectories directories, LauncherSettings settings)
    {
        if (!Enum.IsDefined(directories.Mode) || !Path.IsPathFullyQualified(directories.IsolatedDirectory) || !Path.IsPathFullyQualified(directories.SharedDirectory)) throw new InvalidDataException("实例目录策略无效。");
        var shared = directories.Mode == InstanceDirectoryMode.Shared || directories.Mode == InstanceDirectoryMode.FollowGlobal &&
            (settings.LaunchOptions?.Isolation == InstanceIsolation.Shared || settings.LaunchOptions?.Isolation == InstanceIsolation.Modded && instance.Loader is "vanilla" or "optifine");
        return directories.Mode == InstanceDirectoryMode.Current ? instance.GameDirectory : shared ? directories.SharedDirectory : directories.IsolatedDirectory;
    }
}
