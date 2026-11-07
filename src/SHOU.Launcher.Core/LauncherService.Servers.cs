using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IServerLaunchService
{
    public Task LaunchToServerAsync(string instanceId, string address, CancellationToken cancellationToken = default)
    {
        var destination = ServerLaunchPolicy.Parse(address).Address;
        return ExecuteAsync("Launching", ct => LaunchCoreAsync(instanceId, destination, ct), cancellationToken);
    }
}
