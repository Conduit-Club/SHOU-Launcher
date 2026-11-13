using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;
using System.Diagnostics;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task ReopenedRegistryRetainsBothWindowsOfOneInstanceAfterTheOwnerExits()
    {
        var fixture = new PlatformFixture(); var runner = new ProcessRunner();
        var first = await runner.StartAsync(fixture.JavaProcess("sleep"));
        var second = await runner.StartAsync(fixture.JavaProcess("sleep"));
        using var a = Process.GetProcessById(first.ProcessId); using var b = Process.GetProcessById(second.ProcessId);
        try
        {
            var instance = new GameInstance("same", "Same pack", "fixture", fixture.Root);
            var directory = Path.Combine(fixture.Root, "multiple-registry"); var registry = new GameSessionRegistry(directory);
            registry.Register(instance, first.ProcessId); registry.Register(instance, second.ProcessId);
            Assert.HasCount(2, registry.ReadAll(instance));
            await ((IDetachableProcess)first).DetachAsync(); await ((IDetachableProcess)second).DetachAsync(); await runner.DisposeAsync();
            Assert.IsFalse(a.HasExited); Assert.IsFalse(b.HasExited);
            registry = new GameSessionRegistry(directory); Assert.HasCount(2, registry.ReadAll(instance));
            a.Kill(entireProcessTree: true); await a.WaitForExitAsync();
            Assert.AreEqual(second.ProcessId, registry.ReadAll(instance).Single().ProcessId);
            Assert.ThrowsExactly<InvalidOperationException>(() => registry.EnsureWritable(instance));
        }
        finally
        {
            if (!a.HasExited) { a.Kill(entireProcessTree: true); await a.WaitForExitAsync(); }
            if (!b.HasExited) { b.Kill(entireProcessTree: true); await b.WaitForExitAsync(); }
            await first.DisposeAsync(); await second.DisposeAsync(); await runner.DisposeAsync();
        }
    }
    [TestMethod]
    public async Task FailedSessionRegistrationStopsOnlyTheNewChildBeforePrivateHandoff()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        Process? registered = null;
        using var input = SensitiveProcessInput.CopyFrom("synthetic-private-input"u8);
        var spec = fixture.JavaProcess("sleep") with { SensitiveInput = input, OnStarted = pid => {
            registered = Process.GetProcessById(pid); throw new IOException("Synthetic session registration failure."); } };
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await runner.StartAsync(spec));
            Assert.IsNotNull(registered); Assert.IsTrue(registered.HasExited);
        }
        finally { registered?.Dispose(); }
    }
    [TestMethod]
    public async Task DetachedChildSurvivesRunnerDisposalAndReopenedRegistryProtectsOnlyItsInstance()
    {
        var fixture = new PlatformFixture();
        var runner = new ProcessRunner();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = await runner.StartAsync(fixture.JavaProcess("sleep"), new InlineProgress<ProcessOutput>(o => { if (o.Text.Contains("started")) ready.TrySetResult(); }));
        using var native = Process.GetProcessById(child.ProcessId);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var first = new GameInstance("first", "First", "fixture", fixture.Root);
            var second = first with { Id = "second", GameDirectory = Path.Combine(fixture.Root, "second") };
            var records = Path.Combine(fixture.Root, "registry");
            var registry = new GameSessionRegistry(records);
            registry.Register(first, child.ProcessId);
            await ((IDetachableProcess)child).DetachAsync();
            await runner.DisposeAsync();
            Assert.IsFalse(native.HasExited, "Closing the process owner must leave its committed game alive.");
            var reopened = new GameSessionRegistry(records);
            Assert.AreEqual(child.ProcessId, reopened.Read(first)!.ProcessId);
            Assert.ThrowsExactly<InvalidOperationException>(() => reopened.EnsureWritable(first));
            reopened.EnsureWritable(second);
            // A reused PID never makes an old record active. Disk identities never authorize Stop/Kill.
            File.WriteAllText(Directory.GetFiles(records, "*.json").Single(), System.Text.Json.JsonSerializer.Serialize(new { Pid = child.ProcessId, StartTicks = 1L }));
            Assert.IsNull(reopened.Read(first));
        }
        finally
        {
            if (!native.HasExited) { native.Kill(entireProcessTree: true); await native.WaitForExitAsync(); }
            await child.DisposeAsync(); await runner.DisposeAsync();
        }
    }
    [TestMethod]
    public async Task ArgumentsKeepUnicodeQuotesSpacesAndShellMetacharacters()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        string[] values = ["中文 空格 (game)", "a\"b", "$HOME;$(not-a-command)", @"C:\folder with spaces\", "", "line\nbreak"];
        // Verify the OS argv transport independently of Java's Windows ANSI conversion.
        // Java process startup/draining/cancellation remain covered by the other native fixture tests.
        var result = await runner.RunAsync(fixture.ManagedArgvProcess(values));
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(string.Concat(values.Select(value => value.Length + ":" + value)), result.StandardOutput);
        Assert.IsFalse(result.Cancelled || result.TimedOut);
    }

    [TestMethod]
    public async Task BothPipesAreDrainedWithOneBoundedCapture()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        var reported = 0;
        var result = await runner.RunAsync(fixture.JavaProcess("flood") with { OutputLimitCharacters = 1024 },
            new InlineProgress<ProcessOutput>(output => Interlocked.Add(ref reported, output.Text.Length)));
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.OutputTruncated);
        Assert.AreEqual(1024, result.StandardOutput.Length + result.StandardError.Length);
        Assert.AreEqual(1024, reported);
    }

    [TestMethod]
    public async Task TimeoutTerminatesTheOwnedProcessAndReportsFailure()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        var result = await runner.RunAsync(fixture.JavaProcess("sleep") with { Timeout = TimeSpan.FromSeconds(1) });
        Assert.IsTrue(result.TimedOut);
        Assert.IsFalse(result.Cancelled);
        Assert.AreNotEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task CancellationAndExplicitStopOnlyAffectTheirOwnChild()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await runner.StartAsync(fixture.JavaProcess("sleep"),
            new InlineProgress<ProcessOutput>(output => { if (output.Text.Contains("started")) started.TrySetResult(); }), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var second = await runner.StartAsync(fixture.JavaProcess("sleep"));
        cancellation.Cancel();
        var firstResult = await first.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(firstResult.Cancelled);
        Assert.IsFalse(firstResult.TimedOut);
        Assert.IsFalse(second.Completion.IsCompleted, "An unrelated child must remain running.");
        await second.StopAsync();
        Assert.IsTrue((await second.Completion).Cancelled);
    }

    [TestMethod]
    public async Task EnvironmentOverridesDoNotChangeTheParent()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        const string name = "SHOU_PLATFORM_TEST_VALUE";
        var original = Environment.GetEnvironmentVariable(name);
        var result = await runner.RunAsync(fixture.JavaProcess("env", name) with
        { EnvironmentOverrides = new Dictionary<string, string?> { [name] = "假数据 $literal 中文" } });
        Assert.AreEqual("假数据 $literal 中文", result.StandardOutput);
        Assert.AreEqual(original, Environment.GetEnvironmentVariable(name));
        var removed = await runner.RunAsync(fixture.JavaProcess("env", name) with
        { EnvironmentOverrides = new Dictionary<string, string?> { [name] = null } });
        Assert.AreEqual("null", removed.StandardOutput);
    }

    [TestMethod]
    public async Task RelativeExecutableAndStartupFailureNeverReturnSuccess()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        await Assert.ThrowsAsync<ArgumentException>(async () => await runner.StartAsync(new ProcessSpec("java", [], fixture.Root)));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(async () => await runner.StartAsync(
            new ProcessSpec(fixture.Child("missing-java"), [], fixture.Root)));
    }
}
