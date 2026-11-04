using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
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
            var settings = Snapshot.Settings with { JavaExecutable = result.Java.ExecutablePath };
            var candidate = Snapshot with { Settings = settings,
                JavaInstallations = Snapshot.JavaInstallations.Where(j => !SameJavaPath(j.ExecutablePath, result.Java.ExecutablePath)).Append(result.Java).ToArray() };
            await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
            network.Update(settings); Set(s => s with { Settings = settings,
                JavaInstallations = s.JavaInstallations.Where(j => !SameJavaPath(j.ExecutablePath, result.Java.ExecutablePath)).Append(result.Java).ToArray() });
            Log("Info", "全局默认Java已选择验证后的独立修复副本；实例显式覆盖保持。源Java未修改，可在Java路径设置中选回。策略=" + result.PolicyId);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
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
        var relative = Path.GetRelativePath(derived, home);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("派生Java越过受控runtime根，未选择该副本。");
        return result;
    }
    private bool IsDerivedJava(JavaInstallation java, string? selectedPath = null)
    {
        var root = SafePath.Canonical(DerivedJavaRoot);
        static bool Inside(string boundary, string path)
        {
            var relative = Path.GetRelativePath(boundary, path);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
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
