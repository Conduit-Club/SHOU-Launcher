using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class WpfShutdownContextTests
{
    [TestMethod]
    public async Task ActiveProcessStopAndRunnerDisposalDoNotRequireAStoppedDesktopDispatcher()
    {
        foreach (var disposeRunner in new[] { false, true })
        {
            var fixture = new PlatformFixture();
            await using var runner = new ProcessRunner();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var process = await runner.StartAsync(fixture.JavaProcess("sleep"),
                new InlineProgress<ProcessOutput>(o => { if (o.Text.Contains("started")) started.TrySetResult(); }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(process.Completion.IsCompleted);
            var context = new StoppedDispatcher();
            var previous = SynchronizationContext.Current;
            Task shutdown;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                shutdown = disposeRunner ? runner.DisposeAsync().AsTask() : process.StopAsync();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { context.DrainForFailureCleanup(); }
            Assert.AreEqual(0, context.PostCount, "A stopped dispatcher must not be needed to finish owned process shutdown.");
            var result = await process.Completion;
            Assert.IsTrue(result.Cancelled); Assert.IsFalse(result.TimedOut);
        }
    }
    private sealed class StoppedDispatcher : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queued = new();
        public int PostCount { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) { PostCount++; queued.Enqueue((d, state)); }
        public void DrainForFailureCleanup() { while (queued.TryDequeue(out var item)) item.Callback(item.State); }
    }
}
