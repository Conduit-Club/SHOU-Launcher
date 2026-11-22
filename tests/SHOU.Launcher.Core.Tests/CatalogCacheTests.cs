using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CatalogCacheTests
{
    private static GameCatalog Catalog => new([new("1.21.1", "release", DateTimeOffset.UnixEpoch, true),
        new("Historic Name", "old_alpha", DateTimeOffset.MinValue, CanInstall: false)], "fixture.example");
    [TestMethod]
    public async Task FreshCatalogSurvivesRestartAndExplicitRefreshFetchesAgain()
    {
        var root = TestFiles.Root(); var now = DateTimeOffset.UtcNow; var calls = 0;
        Task<GameCatalog> Fetch(CancellationToken ct) { calls++; return Task.FromResult(Catalog); }
        var cache = new CachedGameCatalog(root, () => now);
        var first = await cache.GetAsync("source-a", false, Fetch);
        Assert.IsFalse(first.FromCache); Assert.AreEqual(now, first.FetchedAt);
        var reopened = new CachedGameCatalog(root, () => now.AddMinutes(1));
        var second = await reopened.GetAsync("source-a", false, Fetch);
        Assert.IsTrue(second.FromCache); Assert.AreEqual(1, calls); Assert.AreEqual("Historic Name", second.Versions[1].Id);
        await reopened.GetAsync("source-a", true, Fetch); Assert.AreEqual(2, calls);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(root, "catalog")).Length);
    }
    [TestMethod]
    public async Task ExpiredAndForcedFailuresReturnDatedCacheButCancellationPropagates()
    {
        var root = TestFiles.Root(); var now = DateTimeOffset.UtcNow; var cache = new CachedGameCatalog(root, () => now);
        await cache.GetAsync("source", false, _ => Task.FromResult(Catalog)); var saved = now;
        now = now.AddDays(2);
        var stale = await cache.GetAsync("source", false, _ => throw new HttpRequestException("secret-url-must-not-be-persisted"));
        Assert.IsTrue(stale.FromCache); Assert.AreEqual(saved, stale.FetchedAt); StringAssert.Contains(stale.Notice!, "无法更新");
        Assert.IsFalse(stale.Notice!.Contains("secret"));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync("source", true, ct =>
        { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Catalog); }, cancellation.Token));
        var forceFailure = await cache.GetAsync("source", true, _ => throw new IOException("offline"));
        Assert.AreEqual(saved, forceFailure.FetchedAt);
    }
    [TestMethod]
    public async Task SourceSettingsHaveSeparateOpaqueKeysAndNoCrossSourceFallback()
    {
        var root = TestFiles.Root(); var cache = new CachedGameCatalog(root);
        const string privateConfiguration = "fixture configuration with api_key=opaque-test-value";
        await cache.GetAsync(privateConfiguration, false, _ => Task.FromResult(Catalog));
        var files = Directory.GetFiles(Path.Combine(root, "catalog"));
        Assert.AreEqual(1, files.Length); Assert.IsFalse(Path.GetFileName(files[0]).Contains("fixture"));
        Assert.IsFalse((await File.ReadAllTextAsync(files[0])).Contains("opaque-test-value"));
        await Assert.ThrowsExactlyAsync<IOException>(() => cache.GetAsync("different source", false, _ => throw new IOException("offline")));
    }
    [TestMethod]
    public async Task CorruptFutureOversizeAndUnsafeInstallationIdentitiesAreNeverUsedAsFallback()
    {
        var root = TestFiles.Root(); var now = DateTimeOffset.UtcNow; var cache = new CachedGameCatalog(root, () => now);
        await cache.GetAsync("source", false, _ => Task.FromResult(Catalog)); var file = Directory.GetFiles(Path.Combine(root, "catalog")).Single();
        var invalid = new[] { "{broken", "null", JsonSerializer.Serialize(Catalog with { FetchedAt = now.AddDays(1) }),
            JsonSerializer.Serialize(Catalog with { FetchedAt = now, Versions = [new("../escape", "release", now)] }),
            JsonSerializer.Serialize(Catalog with { FetchedAt = now, Source = "https://fixture.example/private?key=value" }), new string('x', 8 * 1024 * 1024 + 1) };
        foreach (var json in invalid)
        {
            await File.WriteAllTextAsync(file, json);
            await Assert.ThrowsExactlyAsync<IOException>(() => cache.GetAsync("source", false, _ => throw new IOException("offline")));
        }
        var repaired = await cache.GetAsync("source", false, _ => Task.FromResult(Catalog));
        Assert.IsFalse(repaired.FromCache); Assert.AreEqual(2, repaired.Versions.Count);
    }
    [TestMethod]
    public async Task CacheWriteFailureDoesNotHideAValidRemoteCatalog()
    {
        var root = TestFiles.Root(); await File.WriteAllTextAsync(Path.Combine(root, "catalog"), "occupied");
        var result = await new CachedGameCatalog(root).GetAsync("source", false, _ => Task.FromResult(Catalog));
        Assert.IsFalse(result.FromCache); Assert.AreEqual(2, result.Versions.Count); StringAssert.Contains(result.Notice!, "缓存未保存");
    }
}
