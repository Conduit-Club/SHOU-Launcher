namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Explicit, foreground authorization of existing protected records; never used by unattended startup.</summary>
public interface IUserAuthorizedCredentialStore
{
    Task<byte[]?> ReadWithUserAuthorizationAsync(string key, CancellationToken cancellationToken = default);
}
public interface ICredentialAccessRecoveryService
{
    bool CanAuthorizeCredentialAccess { get; }
    Task RecoverCredentialAccessAsync(CancellationToken cancellationToken = default);
}
