using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadRateMeterTests
{
    [TestMethod]
    public void ShortParallelFilesAndFinalChunksCountOnceAndAgeOut()
    {
        var now = TimeSpan.Zero; var meter = new DownloadRateMeter(() => now);
        Assert.AreEqual(0, meter.Report(new("a", "Queued", 0, 200)));
        Assert.AreEqual(100, meter.Report(new("a", "Downloading", 100, 200)));
        Assert.AreEqual(100, meter.Report(new("a", "Downloading", 100, 200)));
        now = TimeSpan.FromMilliseconds(250);
        Assert.AreEqual(150, meter.Report(new("b", "Completed", 50, 50, "cdn")));
        Assert.AreEqual(250, meter.Report(new("a", "Completed", 200, 200, "cdn")));
        now = TimeSpan.FromMilliseconds(1000);
        Assert.AreEqual(150, meter.Report(new("c", "Queued", 0, 100)));
        now = TimeSpan.FromMilliseconds(1250);
        Assert.AreEqual(0, meter.Report(new("c", "Queued", 0, 100)));
    }
    [TestMethod]
    public void IgnoredResumeRangeStartsANewBaselineAndLocalStagesAreExcluded()
    {
        var meter = new DownloadRateMeter(() => TimeSpan.Zero);
        Assert.AreEqual(0, meter.Report(new("a", "Queued", 900, 1000, "cdn")));
        Assert.AreEqual(0, meter.Report(new("a", "Queued", 0, 1000, "cdn")));
        Assert.AreEqual(100, meter.Report(new("a", "Downloading", 100, 1000, "cdn")));
        Assert.AreEqual(100, meter.Report(new("local", "Verifying", 9000, 10000)));
    }
    [TestMethod]
    public void CacheResumeOffsetsAndOperationResetDoNotInventNetworkTraffic()
    {
        var meter = new DownloadRateMeter(() => TimeSpan.Zero);
        Assert.AreEqual(0, meter.Report(new("cache", "Completed", 10000, 10000, "cache")));
        Assert.AreEqual(0, meter.Report(new("resume", "Queued", 900, 1000, "cdn")));
        Assert.AreEqual(50, meter.Report(new("resume", "Downloading", 950, 1000, "cdn")));
        Assert.AreEqual(100, meter.Report(new("resume", "Completed", 1000, 1000, "cdn")));
        meter.Reset(); Assert.AreEqual(0, meter.Report(new("next", "Queued", 0, 1000)));
    }
}
