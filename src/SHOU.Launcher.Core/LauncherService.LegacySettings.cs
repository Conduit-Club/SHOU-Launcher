using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILegacyInstanceSettingsService
{
    public async Task<LegacyInstanceSettings> ReadLegacyInstanceSettingsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); var instance = FindInstance(instanceId);
        var origin = instance.Origin ?? throw new InvalidOperationException("此实例没有旧版来源目录。");
        _ = await ExistingInstanceReader.ResolveAsync(origin, instance.VersionId, InstanceIdentity.GameVersion(instance), cancellationToken);
        var mapped = LegacyLaunchSettings.Map(await LegacyLaunchSettings.ReadAsync(origin.VersionDirectory, cancellationToken),
            GameSettingsPolicy.Options(instance, Snapshot.Settings), Paths.ResourcesDirectory);
        if (mapped.MemoryMiB is { } maximum && mapped.Options.MinimumMemoryMiB is null && Snapshot.Settings.MinimumMemoryMiB > maximum)
            mapped = mapped with { Options = mapped.Options with { MinimumMemoryMiB = 512 } };
        return mapped;
    }

    private async Task<GameInstance> ImportLegacyRuntimeSettingsAsync(GameInstance instance, LauncherSettings settings,
        string? executableDirectory, CancellationToken ct)
    {
        try
        {
            var values = await LegacyLaunchSettings.ReadAsync(instance.Origin!.VersionDirectory, ct).ConfigureAwait(false);
            var basis = settings.LaunchOptions ?? new();
            var mapped = LegacyLaunchSettings.Map(values, basis, executableDirectory);
            foreach (var warning in mapped.Warnings) Log("Warning", warning);
            GameLaunchOptions? options = null;
            if (mapped.MemoryMiB is not null && (mapped.Options.MinimumMemoryMiB is not null || settings.MinimumMemoryMiB is not null))
                options = basis with { InheritGlobal = false, MinimumMemoryMiB = Math.Min(mapped.Options.MinimumMemoryMiB ?? 512, mapped.MemoryMiB.Value) };
            return instance with { JavaExecutable = mapped.JavaExecutable, MemoryMiB = mapped.MemoryMiB, LaunchOptions = options };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Log("Warning", "旧实例运行偏好暂不可读；实例与原文件保留，未自动覆盖 Java 或内存。");
            return instance;
        }
    }
}
