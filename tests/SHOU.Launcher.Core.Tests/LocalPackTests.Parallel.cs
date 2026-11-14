using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PackTransfersAreBoundedAndCancellationWaitsForActiveWriters(bool cancel)
    {
        var assets = Enumerable.Range(0, 6).Select(i => Mod() with { Project = 101 + i, FileId = 1001 + i, Name = "fixture-" + i + ".jar" }).ToArray();
        var source = Archive(Manifest(assets)); var read = await new LocalPackArchive().ReadAsync(source);
        using var handler = new FixtureApi(assets); using var http = new HttpClient(handler);
        var result = await new CurseForgePackResolver(http, FixtureKey).ResolveAsync(read.CurseForgeFiles, "1.21.1", "fabric"); Assert.IsEmpty(result.BlockedFiles);
        var concurrencyLock = new object(); var active = 0; var entered = 0; var maximum = 0; var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        async Task Download(CurseForgePackFile file, string path, CancellationToken token)
        {
            var count = Interlocked.Increment(ref active); lock (concurrencyLock) maximum = Math.Max(maximum, count);
            try
            {
                if (Interlocked.Increment(ref entered) == 2) { gate.SetResult(); if (cancel) cancellation.Cancel(); }
                await gate.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested(); await File.WriteAllBytesAsync(path, assets.Single(a => a.FileId == file.FileId).Bytes, token);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var stage = Path.Combine(TestFiles.Root(), "stage");
        var operation = new LocalPackArchive().StageCurseForgeAsync(source, read.Inspection.ArchiveSha256, stage, result.Files, Download, cancellation.Token, maximumConnections: 2);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        else { await operation.WaitAsync(TimeSpan.FromSeconds(10)); Assert.AreEqual(6, Directory.GetFiles(Path.Combine(stage, "mods")).Length); }
        Assert.AreEqual(2, maximum); Assert.AreEqual(0, active);
    }
}
