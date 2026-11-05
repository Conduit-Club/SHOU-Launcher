namespace SHOU.Launcher.Core.Abstractions;

public sealed record LoaderVersionChoice(string Loader, string Version, bool Stable, string? Detail = null, bool CanInstall = true);
public interface ILoaderInstanceService
{
    Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion,
        CancellationToken cancellationToken = default);
    Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion,
        CancellationToken cancellationToken = default);
}
