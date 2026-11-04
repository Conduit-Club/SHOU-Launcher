namespace SHOU.Launcher.Core.Abstractions;

public sealed record JavaTrustFinding(string Path, bool IsTrustStore, string Hash, bool NeedsRepair,
    bool CanRepair, string Details, bool CanRestore = false);

/// <summary>The source is read-only. Only a new owned variant below DerivedRoot may be changed.</summary>
public sealed record JavaTrustCopyRequest(JavaInstallation Source, string DerivedRoot,
    string? SourceManifestSha1 = null);

public sealed record JavaTrustDerivation(JavaInstallation Java, string SourceJavaExecutable, string PolicyId,
    string SourceTreeSha256, string OutputTreeSha256, string ProvenancePath,
    IReadOnlyList<JavaTrustFinding> Findings);

public interface IJavaTrustService
{
    Task<IReadOnlyList<JavaTrustFinding>> InspectAsync(JavaInstallation java, CancellationToken cancellationToken = default);
    // Reuse must revalidate source/output/provenance. A supplied source already inside DerivedRoot
    // must be verified as a completed owned variant, never recursively copied or accepted raw.
    Task<JavaTrustDerivation> CreateRepairedCopyAsync(JavaTrustCopyRequest request,
        CancellationToken cancellationToken = default);
}
