using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class JavaBootstrapProcessTests
{
    private static readonly Lazy<Task<NativeFixture>> Fixture = new(NativeFixture.CreateAsync);

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public async Task RealJava8BytecodeEchoKeepsSecretsOutOfOsAndHotSpotCommandLines()
    {
        var fixture = await Fixture.Value;
        await using var runner = new ProcessRunner();
        var secret = "synthetic-private-" + Guid.NewGuid().ToString("N");
        string[] values = ["hold", secret, "中文 😀 \" \\ \n\t# @ $(literal);", ""];
        using var payload = JavaBootstrapPayload.Create("SecureEcho", values);
        using var input = SensitiveProcessInput.CopyFrom(payload.Bytes.Span);
        var owned = OwnedBytes(input);
        var capture = new Capture();
        await using var child = await runner.StartAsync(fixture.Bootstrap(input), capture);
        await capture.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var diagnostic = await runner.RunAsync(new(fixture.Jcmd, [child.ProcessId.ToString(), "VM.command_line"], fixture.Root,
            fixture.Environment, Timeout: TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, diagnostic.ExitCode);
        Assert.IsTrue(diagnostic.StandardOutput.Contains("org.shou.launcher.bootstrap.BootstrapMain", StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.StandardOutput.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.StandardOutput.Contains("SecureEcho", StringComparison.Ordinal));
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("OS argv native assertion in this gate currently targets macOS; Windows requires its own native evidence.");
        var argv = await runner.RunAsync(new("/bin/ps", ["-ww", "-p", child.ProcessId.ToString(), "-o", "command="], fixture.Root,
            Timeout: TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, argv.ExitCode);
        Assert.IsFalse(argv.StandardOutput.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(argv.StandardOutput.Contains("SecureEcho", StringComparison.Ordinal));
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
            StringAssert.Contains(capture.Text, "A" + index + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant());
        StringAssert.Contains(capture.Text, "THREAD:main");
        StringAssert.Contains(capture.Text, "STDIN_CLOSED:true");
        Assert.IsFalse(capture.Text.Contains(secret, StringComparison.Ordinal));
        AssertCleared(owned);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.StartAsync(fixture.Bootstrap(input)));
        await child.StopAsync();
        Assert.IsTrue((await child.Completion).Cancelled);
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public async Task MalformedFramesAndTargetExceptionsNeverEchoThePayload()
    {
        var fixture = await Fixture.Value;
        await using var runner = new ProcessRunner();
        const string secret = "synthetic-not-a-real-token";
        using var valid = JavaBootstrapPayload.Create("SecureEcho", ["throw", secret]);
        var wrongMagic = valid.Bytes.ToArray(); wrongMagic[0] = 0;
        var wrongVersion = valid.Bytes.ToArray(); wrongVersion[9] = 2;
        var trailing = valid.Bytes.ToArray().Concat(new byte[] { 1 }).ToArray();
        var truncated = valid.Bytes.Slice(0, valid.ByteLength - 1).ToArray();
        var badUtf8 = valid.Bytes.ToArray(); badUtf8[18] = 0xff;
        foreach (var (bytes, expected) in new[] { (wrongMagic, 64), (wrongVersion, 64), (trailing, 64), (truncated, 64), (badUtf8, 64), (valid.Bytes.ToArray(), 70) })
        {
            using var input = SensitiveProcessInput.CopyFrom(bytes); var owned = OwnedBytes(input);
            try
            {
                var result = await runner.RunAsync(fixture.Bootstrap(input));
                Assert.AreEqual(expected, result.ExitCode);
                Assert.IsFalse((result.StandardError + result.StandardOutput).Contains(secret, StringComparison.Ordinal));
                StringAssert.StartsWith(result.StandardError, "SHOU bootstrap:");
            }
            catch (IOException failure) { Assert.IsFalse(failure.ToString().Contains(secret, StringComparison.Ordinal)); }
            AssertCleared(owned);
        }
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public async Task InputWritesAndBoundedOutputPumpsProceedConcurrently()
    {
        var fixture = await Fixture.Value;
        await using var runner = new ProcessRunner();
        using var input = SensitiveProcessInput.CopyFrom(new byte[1024 * 1024], TimeSpan.FromSeconds(10));
        var owned = OwnedBytes(input);
        var result = await runner.RunAsync(fixture.Raw("floodread", "1048576") with { SensitiveInput = input, OutputLimitCharacters = 1024 });
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.OutputTruncated);
        Assert.AreEqual(1024, result.StandardOutput.Length + result.StandardError.Length);
        AssertCleared(owned);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("JavaArgumentProtection")]
    public async Task FailedInputTransferStopsOnlyItsChildAndClearsBytes(bool cancel)
    {
        var fixture = await Fixture.Value;
        await using var runner = new ProcessRunner();
        await using var unrelated = await runner.StartAsync(fixture.Raw("sleep"));
        using var cancellation = new CancellationTokenSource();
        var capture = new Capture(cancel ? cancellation : null);
        using var input = SensitiveProcessInput.CopyFrom(new byte[1024 * 1024], TimeSpan.FromSeconds(cancel ? 15 : 1));
        var owned = OwnedBytes(input);
        var timer = Stopwatch.StartNew();
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(async () => await runner.StartAsync(fixture.Raw("neverread") with { SensitiveInput = input }, capture, cancellation.Token));
        else await Assert.ThrowsAsync<TimeoutException>(async () => await runner.StartAsync(fixture.Raw("neverread") with { SensitiveInput = input }, capture));
        Assert.IsLessThan(TimeSpan.FromSeconds(10), timer.Elapsed);
        var pid = await capture.Pid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { using var exited = Process.GetProcessById(pid); Assert.IsTrue(exited.HasExited); }
        catch (ArgumentException) { /* The confirmed fixture child no longer exists. */ }
        Assert.IsFalse(unrelated.Completion.IsCompleted);
        AssertCleared(owned);
        await unrelated.StopAsync();
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public async Task StartupFailureClearsConsumedInputWithoutFallback()
    {
        var fixture = await Fixture.Value;
        await using var runner = new ProcessRunner();
        using var input = SensitiveProcessInput.CopyFrom("synthetic-only"u8);
        var owned = OwnedBytes(input);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(async () => await runner.StartAsync(
            new(Path.Combine(fixture.Root, "missing-executable"), [], fixture.Root, SensitiveInput: input)));
        AssertCleared(owned);
        Assert.Throws<InvalidOperationException>(() => input.ConsumeOwnedBytes());
    }

    private static byte[] OwnedBytes(SensitiveProcessInput input) => (byte[])typeof(SensitiveProcessInput)
        .GetField("bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
    private static void AssertCleared(byte[] bytes) => Assert.IsTrue(bytes.All(value => value == 0));

    private sealed class Capture(CancellationTokenSource? cancel = null) : IProgress<ProcessOutput>
    {
        private readonly StringBuilder text = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Pid { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get { lock (text) return text.ToString(); } }
        public void Report(ProcessOutput output)
        {
            lock (text)
            {
                text.Append(output.Text);
                foreach (var line in text.ToString().Split('\n'))
                    if (line.StartsWith("PID:", StringComparison.Ordinal) && int.TryParse(line[4..].Trim(), out var pid))
                        if (Pid.TrySetResult(pid)) cancel?.Cancel();
                if (text.ToString().Contains("READY", StringComparison.Ordinal)) Ready.TrySetResult();
            }
        }
    }

    private sealed record NativeFixture(string Root, string Java, string Jcmd, string Classes, string Jar, IReadOnlyDictionary<string, string?> Environment)
    {
        public ProcessSpec Bootstrap(SensitiveProcessInput input) => new(Java,
            ["-XX:-UsePerfData", "-Djava.io.tmpdir=" + Root, "-cp", Jar + Path.PathSeparator + Classes, "org.shou.launcher.bootstrap.BootstrapMain"],
            Root, Environment, Timeout: TimeSpan.FromSeconds(30), SensitiveInput: input);
        public ProcessSpec Raw(params string[] args) => new(Java, new[] { "-XX:-UsePerfData", "-Djava.io.tmpdir=" + Root, "-cp", Classes, "SecureEcho" }.Concat(args).ToArray(),
            Root, Environment, Timeout: TimeSpan.FromSeconds(30));
        private static string Source([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", "src", "SHOU.Launcher.Core", "Java", "Bootstrap", "BootstrapMain.java"));
        public static async Task<NativeFixture> CreateAsync()
        {
            var taskRoot = System.Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Native Java input tests require control/dev.py.");
            var home = System.Environment.GetEnvironmentVariable("JAVA_HOME") ?? throw new InvalidOperationException("Registered Java tool is missing.");
            Assert.IsTrue(Path.GetFullPath(home).StartsWith(Path.Combine(taskRoot, "tools", "java") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var root = Path.Combine(taskRoot, "test-data", "platform", "B6", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var classes = Path.Combine(root, "classes"); Directory.CreateDirectory(classes);
            var source = Path.Combine(root, "SecureEcho.java");
            await File.WriteAllTextAsync(source, """
                import java.io.*;
                import java.lang.management.ManagementFactory;
                import java.nio.charset.StandardCharsets;
                import java.security.MessageDigest;
                public class SecureEcho {
                    public static void main(String[] args) throws Exception {
                        if (args[0].equals("floodread")) {
                            String line = new String(new char[128]).replace('\0', 'X');
                            for (int i=0;i<4096;i++) { System.out.print(line); System.err.print(line); }
                            long count=0; byte[] buf=new byte[65536]; int n;
                            while ((n=System.in.read(buf))!=-1) count+=n;
                            if (count!=Long.parseLong(args[1])) System.exit(22);
                            return;
                        }
                        System.out.println("PID:"+ManagementFactory.getRuntimeMXBean().getName().split("@")[0]);
                        if (args[0].equals("neverread") || args[0].equals("sleep")) { System.out.flush(); Thread.sleep(30000); return; }
                        if (args[0].equals("throw")) throw new IllegalArgumentException(args[1]);
                        for (int i=0;i<args.length;i++) {
                            byte[] hash=MessageDigest.getInstance("SHA-256").digest(args[i].getBytes(StandardCharsets.UTF_8));
                            StringBuilder hex=new StringBuilder(); for(byte b:hash)hex.append(String.format("%02x",b&255));
                            System.out.println("A"+i+":"+hex);
                        }
                        System.out.println("THREAD:"+Thread.currentThread().getName());
                        boolean closed=false; try { closed=System.in.read()==-1; } catch(IOException e) { closed=true; }
                        System.out.println("STDIN_CLOSED:"+closed); System.out.println("READY"); System.out.flush();
                        if (args[0].equals("hold")) Thread.sleep(30000);
                    }
                }
                """);
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var java = Path.Combine(home, "bin", "java" + extension); var jcmd = Path.Combine(home, "bin", "jcmd" + extension);
            var javac = Path.Combine(home, "bin", "javac" + extension); var jar = Path.Combine(home, "bin", "jar" + extension);
            var environment = new Dictionary<string, string?> { ["TMPDIR"] = root, ["TMP"] = root, ["TEMP"] = root,
                ["JAVA_TOOL_OPTIONS"] = null, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null, ["_JAVA_LAUNCHER_DEBUG"] = null };
            await using var runner = new ProcessRunner();
            string[] compile = ["-J-XX:-UsePerfData", "-J-Djava.io.tmpdir=" + root, "--release", "8", "-d", classes, Source(), source];
            var compiled = await runner.RunAsync(new(javac, compile, root, environment, Timeout: TimeSpan.FromSeconds(30)));
            Assert.AreEqual(0, compiled.ExitCode, "Controlled javac --release 8 failed.");
            var classFile = Path.Combine(classes, "org", "shou", "launcher", "bootstrap", "BootstrapMain.class");
            var classBytes = await File.ReadAllBytesAsync(classFile); Assert.AreEqual(52, (classBytes[6] << 8) | classBytes[7]);
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(classFile)!, "BootstrapMain$LaunchInput.class")));
            var jarFile = Path.Combine(root, "shou-java-bootstrap.jar");
            string[] package = ["-J-XX:-UsePerfData", "-J-Djava.io.tmpdir=" + root, "--create", "--file", jarFile, "-C", classes, "org/shou/launcher/bootstrap"];
            var packaged = await runner.RunAsync(new(jar, package, root, environment, Timeout: TimeSpan.FromSeconds(20)));
            Assert.AreEqual(0, packaged.ExitCode, "Controlled bootstrap jar creation failed.");
            await File.WriteAllTextAsync(Path.Combine(root, "native-fixture.json"), JsonSerializer.Serialize(new { javac, compile, compileExit = compiled.ExitCode,
                jar, package, packageExit = packaged.ExitCode, root, classMajor = 52, helperEntry = "org.shou.launcher.bootstrap.BootstrapMain",
                helperSourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Source()))).ToLowerInvariant(),
                jarSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(jarFile))).ToLowerInvariant() }, new JsonSerializerOptions { WriteIndented = true }));
            return new(root, java, jcmd, classes, jarFile, environment);
        }
    }
}
