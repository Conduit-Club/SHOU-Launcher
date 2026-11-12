namespace SHOU.Launcher.Core.Abstractions;

public interface IContentDescriptionService
{
    Task<string?> GetChineseContentDescriptionAsync(ContentProject project, CancellationToken cancellationToken = default);
}
