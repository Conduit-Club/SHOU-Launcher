using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IJavaTrustWorkflowService
{
    private string DerivedJavaRoot => SafePath.Within(Paths.RuntimesDirectory, "derived-trust");
    public async Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JavaTrustFinding>? result = null;
        await ExecuteAsync("CheckingJavaTrust", async ct =>
        {
            var java = await RequireJavaForTrustAsync(javaExecutable, ct).ConfigureAwait(false);
            result = await javaTrust.InspectAsync(java, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaTrustDerivation? result = null;
        await ExecuteAsync("RepairingJavaTrust", async ct =>
        {
            var java = await RequireJavaForTrustAsync(javaExecutable, ct).ConfigureAwait(false);
            result = await DeriveJavaAsync(java, ct).ConfigureAwait(false);
            await SaveTrustJavaSelectionAsync(result.Java, ct, new(Snapshot.Settings.JavaExecutable, result.Java.ExecutablePath)).ConfigureAwait(false);
            Log("Info", "全局默认Java已选择验证后的独立修复副本；实例显式覆盖保持。源Java未修改，可在Java路径设置中选回。策略=" + result.PolicyId);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<JavaTrustDerivation> PrepareJavaTrustCopyAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaTrustDerivation? result = null;
        await ExecuteAsync("RepairingJavaTrust", async ct =>
        {
            var java = await RequireJavaForTrustAsync(javaExecutable, ct).ConfigureAwait(false);
            result = await DeriveJavaAsync(java, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Set(s => s with { JavaInstallations = s.JavaInstallations.Where(j => !SameJavaPath(j.ExecutablePath, result.Java.ExecutablePath)).Append(result.Java).ToArray() });
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<JavaTrustSelectionChange> SelectJavaTrustCopyAsync(JavaTrustDerivation prepared, CancellationToken cancellationToken = default)
    {
        JavaTrustSelectionChange? change = null;
        await ExecuteAsync("CheckingJavaTrust", async ct =>
        {
            var java = await platform.JavaDiscovery.ProbeAsync(prepared.Java.ExecutablePath, ct).ConfigureAwait(false);
            if (!java.IsUsable || !IsDerivedJava(java, prepared.Java.ExecutablePath)) throw new InvalidDataException("副本不再是可用的受控 Java，请重新扫描并创建。");
            // Revalidate the completed receipt and both trees at selection time, not only when the copy was prepared.
            var verified = await DeriveJavaAsync(java, ct).ConfigureAwait(false);
            if (!SameJavaPath(verified.Java.ExecutablePath, prepared.Java.ExecutablePath)
                || !SameJavaPath(verified.SourceJavaExecutable, prepared.SourceJavaExecutable)
                || !SameJavaPath(verified.ProvenancePath, prepared.ProvenancePath)
                || verified.PolicyId != prepared.PolicyId || verified.SourceTreeSha256 != prepared.SourceTreeSha256 || verified.OutputTreeSha256 != prepared.OutputTreeSha256)
                throw new InvalidDataException("副本、源 Java 或来源记录已变化，请重新创建并核验。");
            change = new(Snapshot.Settings.JavaExecutable, verified.Java.ExecutablePath);
            await SaveTrustJavaSelectionAsync(verified.Java, ct, change).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return change!;
    }
    public Task RestoreJavaTrustSelectionAsync(JavaTrustSelectionChange change, CancellationToken cancellationToken = default)
        => ExecuteAsync("CheckingJavaTrust", async ct =>
        {
            if (!SameJavaPath(Snapshot.Settings.JavaExecutable ?? "", change.SelectedJavaExecutable))
                throw new InvalidOperationException("默认 Java 已在其它位置更改，本次回退未覆盖新的选择。");
            JavaInstallation? previous = null;
            if (!string.IsNullOrWhiteSpace(change.PreviousJavaExecutable)) previous = await RequireJavaForTrustAsync(change.PreviousJavaExecutable, ct).ConfigureAwait(false);
            await SaveTrustJavaSelectionAsync(previous, ct).ConfigureAwait(false);
        }, cancellationToken);
    private async Task SaveTrustJavaSelectionAsync(JavaInstallation? java, CancellationToken ct, JavaTrustSelectionChange? selection = null)
    {
        ct.ThrowIfCancellationRequested();
        var settings = Snapshot.Settings with { JavaExecutable = java?.ExecutablePath, JavaTrustSelection = selection };
        var candidate = Snapshot with { Settings = settings, JavaInstallations = java is null ? Snapshot.JavaInstallations
            : Snapshot.JavaInstallations.Where(j => !SameJavaPath(j.ExecutablePath, java.ExecutablePath)).Append(java).ToArray() };
        await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
        network.Update(settings); Set(s => s with { Settings = settings, JavaInstallations = candidate.JavaInstallations });
    }
    private async Task<JavaInstallation> RequireJavaForTrustAsync(string executable, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Java路径必须是绝对路径。");
        var probe = await platform.JavaDiscovery.ProbeAsync(executable, ct).ConfigureAwait(false);
        if (!probe.IsUsable) throw new InvalidOperationException("Java无法通过真实probe，未检查或修改证书。");
        var known = Snapshot.JavaInstallations.FirstOrDefault(candidate => SameJavaPath(candidate.ExecutablePath, probe.ExecutablePath));
        var java = known is null ? probe with { Source = "manual:local" }
            : probe with { Source = known.Source, ValidatedManifestSha1 = known.ValidatedManifestSha1 };
        if (IsDerivedJava(java, executable)) java = (await DeriveJavaAsync(java, ct).ConfigureAwait(false)).Java;
        return java;
    }
    private async Task<JavaTrustDerivation> DeriveJavaAsync(JavaInstallation java, CancellationToken ct)
    {
        var result = await javaTrust.CreateRepairedCopyAsync(new(java, DerivedJavaRoot, java.ValidatedManifestSha1), ct).ConfigureAwait(false);
        if (!result.Java.IsUsable || result.Java.MajorVersion != java.MajorVersion || result.Java.RuntimeArchitecture != java.RuntimeArchitecture)
            throw new InvalidDataException("派生Java的版本或架构验证失败，未选择该副本。");
        var derived = SafePath.Canonical(DerivedJavaRoot);
        var home = SafePath.Canonical(result.Java.JavaHome);
        if (!SafePath.IsWithin(derived, home) || !SafePath.IsWithin(derived, SafePath.Canonical(result.Java.ExecutablePath)))
            throw new IOException("派生Java越过受控runtime根，未选择该副本。");
        return result;
    }
    private bool IsDerivedJava(JavaInstallation java, string? selectedPath = null)
    {
        var root = SafePath.Canonical(DerivedJavaRoot);
        static bool Inside(string boundary, string path) => SafePath.IsWithin(boundary, path);
        var home = SafePath.Canonical(java.JavaHome);
        var executable = SafePath.Canonical(java.ExecutablePath);
        var actualHomeInside = Inside(root, home); var actualExecutableInside = Inside(root, executable);
        var literalInside = Inside(root, Path.GetFullPath(java.JavaHome)) || Inside(root, Path.GetFullPath(java.ExecutablePath))
            || selectedPath is not null && Inside(root, Path.GetFullPath(selectedPath));
        if (!literalInside && !actualHomeInside && !actualExecutableInside) return false;
        if (!actualHomeInside || !actualExecutableInside)
            throw new IOException("所选派生Java的可执行文件或java.home逃逸，已拒绝以普通外部Java回退。");
        return true;
    }
    private static bool SameJavaPath(string first, string second) => first.Equals(second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
