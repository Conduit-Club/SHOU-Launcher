using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IAria2ConnectionService
{
    public async Task<Aria2ConnectionStatus> TestAria2ConnectionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await network.Aria2Connection.TestAsync(network, cancellationToken).ConfigureAwait(false);
    }
}
