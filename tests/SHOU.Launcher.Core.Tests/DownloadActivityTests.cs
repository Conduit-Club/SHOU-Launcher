using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadActivityTests
{
    [TestMethod]
    public void ThousandsOfTasksRetainAllBytesAndCacheCountsAcrossRepeatedCompletions()
    {
        var tracker = new DownloadActivityTracker(); tracker.Begin("下载");
        Parallel.For(0, 1200, i =>
        {
            var task = new DownloadTaskSnapshot(i.ToString(), "file", "Completed", 1024, 1024, i % 2 == 0 ? "cache" : "cdn");
            tracker.Report(task); tracker.Report(task);
        });
        tracker.Complete("Completed"); var result = tracker.Capture();
        Assert.AreEqual(1200, result.Totals.Files); Assert.AreEqual(1200, result.Totals.Completed);
        Assert.AreEqual(1200L * 1024, result.Totals.ReadyBytes); Assert.AreEqual(1200L * 1024, result.Totals.KnownTotalBytes);
        Assert.AreEqual(600L * 1024, result.Totals.CachedBytes); Assert.AreEqual(0, result.Totals.Pending);
        Assert.AreEqual(result.Totals, result.History.Single().Statistics);
    }
    [TestMethod]
    public void RetryUnknownSizeVerificationAndCancellationReplacePreviousContribution()
    {
        var tracker = new DownloadActivityTracker(); tracker.Begin("准备");
        var task = new DownloadTaskSnapshot("one", "file", "Downloading", 800, null, "cdn", Connections: 4);
        tracker.Report(task); Assert.AreEqual(1, tracker.Capture().Totals.UnknownSizes); Assert.AreEqual(4, tracker.Capture().Totals.Connections);
        tracker.Report(task with { State = "Retrying", BytesReceived = 0, Connections = 0 });
        Assert.AreEqual(0, tracker.Capture().Totals.ReadyBytes); Assert.AreEqual(0, tracker.Capture().Totals.Connections);
        tracker.Report(task with { State = "Verifying", BytesReceived = 1000, TotalBytes = 1000 });
        Assert.AreEqual(1, tracker.Capture().Totals.Verifying); Assert.AreEqual(0, tracker.Capture().Totals.Completed);
        tracker.Report(task with { State = "Cancelled", BytesReceived = 500 }); tracker.Complete("Cancelled");
        Assert.AreEqual(1, tracker.Capture().Totals.Cancelled); Assert.AreEqual(0, tracker.Capture().Totals.Pending);
        Assert.AreEqual("Cancelled", tracker.Capture().History.Single().State);
    }
    [TestMethod]
    public void CompletedUnknownSizeBecomesKnownAndEmptyOperationsDoNotEraseHistory()
    {
        var tracker = new DownloadActivityTracker(); tracker.Begin("下载");
        tracker.Report(new("one", "file", "Completed", 123)); tracker.Complete("Completed");
        tracker.Begin("保存设置"); tracker.Complete("Completed");
        var state = tracker.Capture(); Assert.AreEqual(123, state.Totals.KnownTotalBytes);
        Assert.AreEqual(0, state.Totals.UnknownSizes); Assert.HasCount(1, state.History); Assert.AreEqual("下载", state.Activity!.Name);
    }
    [TestMethod]
    public void BoundedHistoryDoesNotDropSessionTotalsAndGenerationsRemainSeparate()
    {
        var tracker = new DownloadActivityTracker();
        for (var i = 0; i < 55; i++)
        { tracker.Begin(i.ToString()); tracker.Report(new(i + ":same-file", "same-file", "Completed", 10, 10)); tracker.Complete("Completed"); }
        var state = tracker.Capture(); Assert.HasCount(50, state.History); Assert.AreEqual("54", state.History[0].Name);
        Assert.AreEqual("5", state.History[^1].Name); Assert.AreEqual(55, state.Totals.Files); Assert.AreEqual(550, state.Totals.ReadyBytes);
    }
}
