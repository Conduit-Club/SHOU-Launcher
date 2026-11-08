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
        return LegacyLaunchSettings.Map(await LegacyLaunchSettings.ReadAsync(origin.VersionDirectory, cancellationToken), GameSettingsPolicy.Options(instance, Snapshot.Settings));
    }
}
