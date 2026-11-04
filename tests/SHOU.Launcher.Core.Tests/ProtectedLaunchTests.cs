using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ProtectedLaunchTests
{
    private static (LaunchPlan Plan, JavaBootstrapArtifact Artifact) Fixture()
    {
        var root = TestFiles.Root(); var jar = Path.Combine(root, "引导 空格.jar"); File.WriteAllText(jar, "public fixture bytes");
        var artifact = new JavaBootstrapArtifact(jar, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(jar))));
        var plan = new LaunchPlan("/fixture/java", RuntimeArchitecture.Arm64, root,
            ["-XstartOnFirstThread", "-cp", "/fixture/client.jar", "fixture.GameMain", "--accessToken", "fake-private-session", "--username", "Fixture User"],
            new Dictionary<string, string?> { ["JAVA_HOME"] = "/fixture" }, ["/fixture/client.jar"], ["/fixture/client.jar"], [], [], MainClassIndex: 3);
        return (plan, artifact);
    }
    [TestMethod] public void CredentialsAndTargetGameArgumentsLeavePublicArgvAndReports()
    {
        var (original, artifact) = Fixture();
        using var prepared = ProtectedJavaLaunch.Prepare(original, artifact, ':', "fake-private-session");
        Assert.AreEqual(ProtectedJavaLaunch.BootstrapClass, prepared.Plan.Arguments.Last());
        Assert.IsFalse(prepared.Plan.Arguments.Any(a => a.Contains("fake-private-session", StringComparison.Ordinal) || a.Contains("fixture.GameMain", StringComparison.Ordinal)));
        Assert.AreEqual(2, prepared.Plan.ClasspathEntries.Count);
        Assert.AreEqual(artifact.JarPath, prepared.Plan.ClasspathEntries[0]); // A game library cannot shadow the verified entry class.
        Assert.AreEqual(artifact.Sha256, prepared.Plan.BootstrapSha256);
        Assert.IsFalse(JsonSerializer.Serialize(prepared.Plan.ToRedactedReport()).Contains("fake-private-session", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(new ProcessSpec("/fixture/java", prepared.Plan.Arguments, original.WorkingDirectory, SensitiveInput: prepared.Input)).Contains("fake-private-session", StringComparison.Ordinal));
        var bytes = prepared.Input.ConsumeOwnedBytes();
        try { Assert.IsTrue(Encoding.UTF8.GetString(bytes).Contains("fake-private-session", StringComparison.Ordinal)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Input.ConsumeOwnedBytes());
    }
    [TestMethod] public void ChangedHelperAndCredentialsInJvmOptionsFailClosed()
    {
        var (plan, artifact) = Fixture(); File.AppendAllText(artifact.JarPath, "changed");
        Assert.ThrowsExactly<InvalidDataException>(() => ProtectedJavaLaunch.Prepare(plan, artifact, ':'));
        (plan, artifact) = Fixture();
        var bad = plan with { Arguments = ["-Dprivate=fake-private-session", .. plan.Arguments], MainClassIndex = 4 };
        Assert.ThrowsExactly<InvalidDataException>(() => ProtectedJavaLaunch.Prepare(bad, artifact, ':', "fake-private-session"));
    }
    [TestMethod] public void PrivateInputCopyIsSingleUseAndNeverSerializesItsContents()
    {
        var bytes = Encoding.UTF8.GetBytes("fake-private-bytes");
        using var input = SensitiveProcessInput.CopyFrom(bytes);
        Array.Clear(bytes);
        Assert.IsFalse(input.ToString().Contains("fake-private-bytes", StringComparison.Ordinal));
        var owned = input.ConsumeOwnedBytes();
        try { Assert.AreEqual("fake-private-bytes", Encoding.UTF8.GetString(owned)); }
        finally { CryptographicOperations.ZeroMemory(owned); }
        Assert.ThrowsExactly<InvalidOperationException>(() => input.ConsumeOwnedBytes());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SensitiveProcessInput.CopyFrom([1], TimeSpan.FromSeconds(61)));
    }
    [TestMethod] public void AlternateClasspathAndEntrySelectorsCannotBypassTheVerifiedBootstrap()
    {
        var (plan, artifact) = Fixture();
        foreach (var option in new[] { "--class-path=/untrusted", "-Djava.class.path=/untrusted", "-Djava.system.class.loader=untrusted.Loader", "-Xbootclasspath/a:/untrusted", "@untrusted.args", "-jar", "--module=untrusted/module" })
        {
            var bad = plan with { Arguments = [option, .. plan.Arguments], MainClassIndex = 4 };
            Assert.ThrowsExactly<InvalidDataException>(() => ProtectedJavaLaunch.Prepare(bad, artifact, ':'));
        }
    }
    [TestMethod, TestCategory("JavaArgumentProtection")]
    public async Task RealJvmLoadsVerifiedHelperBeforeAnUntrustedSameNamedEntryClass()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME") ?? throw new InvalidOperationException("Registered Java tool is required.");
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var root = TestFiles.Root(); var evil = Path.Combine(root, "evil"); var target = Path.Combine(root, "target");
        Directory.CreateDirectory(evil); Directory.CreateDirectory(target);
        var evilSource = Path.Combine(root, "BootstrapMain.java"); var targetSource = Path.Combine(root, "ProtectedTarget.java");
        await File.WriteAllTextAsync(evilSource, "package org.shou.launcher.bootstrap; public class BootstrapMain { public static void main(String[] a) { System.out.println(\"SHADOWED\"); } }");
        await File.WriteAllTextAsync(targetSource, "public class ProtectedTarget { public static void main(String[] a) { System.out.println(\"TRUSTED_TARGET\"); } }");
        var environment = new Dictionary<string, string?> { ["TMPDIR"] = root, ["TMP"] = root, ["TEMP"] = root,
            ["JAVA_TOOL_OPTIONS"] = null, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null };
        await using var runner = new ProcessRunner();
        foreach (var (source, classes) in new[] { (evilSource, evil), (targetSource, target) })
        {
            var result = await runner.RunAsync(new(Path.Combine(home, "bin", "javac" + extension),
                ["-J-XX:-UsePerfData", "-J-Djava.io.tmpdir=" + root, "--release", "8", "-d", classes, source], root, environment, Timeout: TimeSpan.FromSeconds(30)));
            Assert.AreEqual(0, result.ExitCode);
        }
        var jar = Path.Combine(root, "trusted-bootstrap.jar");
        await using (var resource = typeof(SHOU.Launcher.Desktop.Bootstrap).Assembly.GetManifestResourceStream("SHOU.Launcher.Desktop.JavaBootstrap.jar")
            ?? throw new InvalidOperationException("Unified test entry must embed the production helper."))
        await using (var output = File.Create(jar)) await resource.CopyToAsync(output);
        var artifact = new JavaBootstrapArtifact(jar, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(jar))));
        var original = new LaunchPlan(Path.Combine(home, "bin", "java" + extension), RuntimeArchitecture.Arm64, root,
            ["-XX:-UsePerfData", "-Djava.io.tmpdir=" + root, "-cp", evil + Path.PathSeparator + target, "ProtectedTarget", "synthetic-secret"],
            environment, [evil, target], [evil, target], [], [], MainClassIndex: 4);
        using var prepared = ProtectedJavaLaunch.Prepare(original, artifact, Path.PathSeparator, "synthetic-secret");
        var executed = await runner.RunAsync(new(original.JavaExecutable, prepared.Plan.Arguments, root, prepared.Plan.EnvironmentOverrides,
            Timeout: TimeSpan.FromSeconds(15), SensitiveInput: prepared.Input));
        Assert.AreEqual(0, executed.ExitCode);
        StringAssert.Contains(executed.StandardOutput, "TRUSTED_TARGET");
        Assert.IsFalse(executed.StandardOutput.Contains("SHADOWED", StringComparison.Ordinal));
        Assert.IsFalse(executed.StandardOutput.Contains("synthetic-secret", StringComparison.Ordinal));
    }
}
