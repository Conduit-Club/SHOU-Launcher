using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Java;

/// <summary>Exact metadata major and actual JVM architecture; a higher major is not an automatic substitute.</summary>
public static class JavaSelection
{
    public static JavaInstallation? Select(IEnumerable<JavaInstallation> installations, int requiredMajor,
        RuntimeArchitecture requiredArchitecture, string? preferredExecutable = null)
    {
        if (requiredMajor < 1 || requiredArchitecture == RuntimeArchitecture.Unknown)
            throw new ArgumentException("A metadata Java major and explicit runtime architecture are required.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = installations.Where(java => java.IsUsable && java.MajorVersion == requiredMajor
            && java.RuntimeArchitecture == requiredArchitecture).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredExecutable))
            return candidates.FirstOrDefault(java => comparer.Equals(java.ExecutablePath, preferredExecutable));
        // Discovery source priority is stable. Do not override a user's preferred runtime with a different vendor.
        return candidates.FirstOrDefault();
    }
}
