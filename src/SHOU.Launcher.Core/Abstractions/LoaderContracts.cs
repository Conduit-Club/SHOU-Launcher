namespace SHOU.Launcher.Core.Abstractions;

public interface ITransactionalInstanceInstaller
{
    Task<GameInstance> CreateInstalledInstanceAsync(string name, string gameVersion, string loader, string? loaderVersion, CancellationToken cancellationToken = default);
}

public interface IInstanceComponentService
{
    Task<GameInstance> ChangeInstanceLoaderAsync(string instanceId, string loader, string? loaderVersion, CancellationToken cancellationToken = default);
}

public sealed record LoaderVersionChoice(string Loader, string Version, bool Stable, string? Detail = null, bool CanInstall = true, string? Caption = null)
{ public string DisplayVersion => Caption ?? Version; }
public interface ILoaderInstanceService
{
    Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion,
        CancellationToken cancellationToken = default);
    Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion,
        CancellationToken cancellationToken = default);
}
