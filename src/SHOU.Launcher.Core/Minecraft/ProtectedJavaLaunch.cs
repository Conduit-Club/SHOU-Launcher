using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;

namespace SHOU.Launcher.Core.Minecraft;

/// <summary>Moves target main/game arguments out of OS argv and HotSpot's java_command.</summary>
public sealed class ProtectedJavaLaunch : IDisposable
{
    public const string BootstrapClass = "org.shou.launcher.bootstrap.BootstrapMain";
    public LaunchPlan Plan { get; }
    public SensitiveProcessInput Input { get; }
    private ProtectedJavaLaunch(LaunchPlan plan, SensitiveProcessInput input) { Plan = plan; Input = input; }
    public static ProtectedJavaLaunch Prepare(LaunchPlan original, JavaBootstrapArtifact artifact, char classpathSeparator, string? accessToken = null)
    {
        if (!Path.IsPathFullyQualified(artifact.JarPath) || artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Java bootstrap identity is invalid.");
        using (var stream = File.OpenRead(artifact.JarPath))
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Trusted Java bootstrap checksum mismatch; private launch is refused.");
        if (original.MainClassIndex < 0 || original.MainClassIndex >= original.Arguments.Count)
            throw new InvalidDataException("Launch plan has no explicit Java/game argument boundary.");
        var jvm = original.Arguments.Take(original.MainClassIndex).ToList();
        if (jvm.Any(value => value.StartsWith("--class-path=", StringComparison.Ordinal)
            || value.StartsWith("-Djava.class.path=", StringComparison.Ordinal)
            || value.StartsWith("-Djava.system.class.loader=", StringComparison.Ordinal)
            || value.StartsWith("-Xbootclasspath", StringComparison.Ordinal)
            || value.StartsWith('@') || value is "-jar" or "-m" or "--module" || value.StartsWith("--module=", StringComparison.Ordinal)))
            throw new InvalidDataException("JVM classpath or entry-point overrides cannot replace the trusted Java bootstrap.");
        if (accessToken is { Length: > 1 } && jvm.Any(value => value.Contains(accessToken, StringComparison.Ordinal)))
            throw new InvalidDataException("A credential entered public JVM options; private launch is refused.");
        var classpaths = jvm.Select((value, index) => (value, index)).Where(p => p.value is "-cp" or "-classpath" or "--class-path").ToArray();
        if (classpaths.Length != 1 || classpaths[0].index + 1 >= jvm.Count)
            throw new InvalidDataException("Trusted Java bootstrap requires one explicit classpath argument.");
        var entries = new[] { artifact.JarPath }.Concat(original.ClasspathEntries.Where(path => !path.Equals(artifact.JarPath, StringComparison.Ordinal))).ToArray();
        jvm[classpaths[0].index + 1] = string.Join(classpathSeparator, entries);
        jvm.Add(BootstrapClass);
        var environment = new Dictionary<string, string?>(original.EnvironmentOverrides);
        foreach (var name in new[] { "JDK_JAVA_OPTIONS", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS" }) environment[name] = null;
        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            if (pair.Key is string name && name.StartsWith("PCL_", StringComparison.Ordinal)) environment[name] = null;
        using var payload = JavaBootstrapPayload.Create(original.Arguments[original.MainClassIndex], original.Arguments.Skip(original.MainClassIndex + 1).ToArray());
        var input = SensitiveProcessInput.CopyFrom(payload.Bytes.Span);
        var plan = original with { Arguments = jvm, EnvironmentOverrides = environment, ClasspathEntries = entries,
            ResolvedLibraries = original.ResolvedLibraries.Append(artifact.JarPath).ToArray(), MainClassIndex = jvm.Count - 1,
            BootstrapSha256 = artifact.Sha256.ToLowerInvariant(),
            RedactedDiagnostics = original.RedactedDiagnostics.Append("Target main and game arguments use one-shot private stdin; no plaintext argument file or raw-argv fallback.").ToArray() };
        return new(plan, input);
    }
    public void Dispose() => Input.Dispose();
}
