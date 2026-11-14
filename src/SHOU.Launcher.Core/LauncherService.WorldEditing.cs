using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceWorldEditService
{
    public async Task<WorldEditState> GetWorldEditStateAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await WorldStore(instanceId, false).GetEditStateAsync(worldDirectoryName, cancellationToken).ConfigureAwait(false);
    }
    public async Task<WorldEditReceipt> SaveWorldMetadataAsync(string instanceId, string worldDirectoryName, string expectedRevision, WorldMetadataEdit edit, CancellationToken cancellationToken = default)
    {
        WorldEditReceipt? result = null;
        await ExecuteAsync("Ready", async ct => result = await WorldStore(instanceId, true).SaveMetadataAsync(worldDirectoryName, expectedRevision, edit, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<WorldEditReceipt> SetWorldDatapacksEnabledAsync(string instanceId, string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackSelection> packs, bool enabled, CancellationToken cancellationToken = default)
    {
        WorldEditReceipt? result = null;
        await ExecuteAsync("Ready", async ct => result = await WorldStore(instanceId, true).SetDatapacksEnabledAsync(worldDirectoryName, expectedRevision, packs, enabled, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
