using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceModRelationsService
{
    public async Task<InstanceModRelations> ReadInstanceModRelationsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var instance = ContentTarget(instanceId, false);
        return await Task.Run(() => ModRelations.Read(instance, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
