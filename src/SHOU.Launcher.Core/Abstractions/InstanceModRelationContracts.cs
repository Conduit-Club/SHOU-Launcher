namespace SHOU.Launcher.Core.Abstractions;

public sealed record ModDeclaredDependency(string Id, string? Requirement, bool Optional = false);
public sealed record ModDeclaration(string Path, string Name, string? Version, string? Loader,
    IReadOnlyList<string> Ids, IReadOnlyList<ModDeclaredDependency> Dependencies,
    IReadOnlyList<ModDeclaration> Children, IReadOnlyList<string> Notices);
public sealed record ModRelationHost(string RelativePath, bool Enabled, ModDeclaration Declaration);
// These states describe metadata presence, not the loader's version selection or compatibility.
public enum ModDeclarationPresence { Independent, Embedded, Disabled, NotFound, Unverified }
public sealed record ModDependencyRelation(string HostPath, string OwnerPath, string OwnerName,
    bool OwnerEnabled, ModDeclaredDependency Dependency, ModDeclarationPresence Presence,
    IReadOnlyList<string> Providers);
public sealed record InstanceModRelations(IReadOnlyList<ModRelationHost> Hosts,
    IReadOnlyList<ModDependencyRelation> Dependencies, IReadOnlyList<string> Notices);
public interface IInstanceModRelationsService
{
    Task<InstanceModRelations> ReadInstanceModRelationsAsync(string instanceId, CancellationToken cancellationToken = default);
}
