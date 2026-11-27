using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Diagnostics;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CrashAnalysisTests
{
    [TestMethod]
    [DataRow("The driver does not appear to support OpenGL", "UnsupportedOpenGl")]
    [DataRow("java.lang.OutOfMemoryError: Java heap space", "OutOfMemory")]
    [DataRow("OpenJ9 is incompatible", "UsingOpenJ9")]
    [DataRow("java.lang.ClassNotFoundException: org.spongepowered.asm.launch.MixinTweaker", "MissingMixinBootstrap")]
    [DataRow("because module java.base does not export", "JavaTooNew")]
    [DataRow("Extracted mod jars found, loading will NOT continue", "ExtractedModFile")]
    public async Task OriginalRulesAndLocalizedReasonsAreUsed(string log, string cause)
    {
        var path = Path.Combine(TestFiles.Root(), "latest.log"); await File.WriteAllTextAsync(path, log);
        var report = await CrashAnalysis.AnalyzeAsync([path]);
        Assert.IsTrue(report.Findings.Any(f => f.Cause == cause), string.Join(",", report.Findings.Select(f => f.Cause)));
        Assert.IsFalse(report.Text.Contains("Crash.Reason.", StringComparison.Ordinal)); Assert.IsTrue(report.Text.Length > 10);
        var english = await CrashAnalysis.AnalyzeAsync([path], "en-US"); Assert.AreNotEqual(report.Text, english.Text);
    }
    [TestMethod]
    public async Task ReportZipAnalyzesWithoutExtractingPathsAndExportsFullRedactedLogs()
    {
        var root = TestFiles.Root(); var source = Path.Combine(root, "report.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("../../latest.log").Open()); await writer.WriteAsync("java.lang.OutOfMemoryError: Java heap space\naccessToken=secret-value\n" + new string('x', 40000)); }
        var report = await CrashAnalysis.AnalyzeAsync([source]); Assert.IsTrue(report.Findings.Any(f => f.Cause == "OutOfMemory"));
        var destination = Path.Combine(root, "output.zip"); await CrashAnalysis.ExportAsync(report, destination);
        using var exported = ZipFile.OpenRead(destination); Assert.IsTrue(exported.Entries.All(e => !e.FullName.Contains("..", StringComparison.Ordinal)));
        using var reader = new StreamReader(exported.Entries.Single(e => e.FullName.StartsWith("logs/", StringComparison.Ordinal)).Open()); var full = await reader.ReadToEndAsync();
        Assert.IsFalse(full.Contains("secret-value", StringComparison.Ordinal)); Assert.IsTrue(full.Contains(new string('x', 40000), StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(root, "latest.log")));
    }
    [TestMethod]
    public async Task UnknownLogsAndCancellationDoNotInventDiagnosis()
    {
        var path = Path.Combine(TestFiles.Root(), "arbitrary.txt"); await File.WriteAllTextAsync(path, "ordinary game output without crash signature");
        var report = await CrashAnalysis.AnalyzeAsync([path]); Assert.IsFalse(report.Findings.Any(f => f.Cause is "OutOfMemory" or "JavaTooNew" or "UnsupportedOpenGl"), string.Join(",", report.Findings.Select(f => f.Cause))); Assert.IsTrue(report.Text.Length > 0);
        using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => CrashAnalysis.AnalyzeAsync([path], ct: cts.Token));
    }
}
