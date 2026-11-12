namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstanceContentOnlineInfo(string RelativePath, ContentProject Project, string Version);
public sealed record InstanceContentInfoCheck(IReadOnlyList<InstanceContentOnlineInfo> Info, IReadOnlyList<string> Notices);
public interface IInstanceContentMetadataService
{
    Task<InstanceContentInfoCheck> ReadInstanceContentMetadataAsync(string instanceId, string kind, CancellationToken cancellationToken = default);
}
