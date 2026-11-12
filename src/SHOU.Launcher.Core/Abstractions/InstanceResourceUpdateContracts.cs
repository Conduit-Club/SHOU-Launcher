namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Resource-pack/shader updates share the existing file-update DTOs and transaction results.</summary>
public interface IInstanceResourceUpdateService
{
    Task<InstanceModUpdateCheck> CheckInstanceResourceUpdatesAsync(string instanceId, string kind, bool refresh = false,
        IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<InstanceModUpdateResult> ApplyInstanceResourceUpdateAsync(string token, CancellationToken cancellationToken = default);
}
