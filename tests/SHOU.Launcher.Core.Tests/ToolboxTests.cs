using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ToolboxTests
{
    [TestMethod]
    public void OfflineUuidsMatchJavaAndLegacyGoldenIdentityAndNormalizeCustomValues()
    {
        Assert.AreEqual("5627dd98e6be3c21b8a8e92344183641", OfflineIdentity.Create("Steve"));
        // v1.5 GetHash + prefix/version/variant; this is not Guid(byte[])'s little-endian representation.
        Assert.AreEqual("0000000000003005998f503099dcf29b", OfflineIdentity.Create("Steve", OfflineUuidMode.Legacy));
        Assert.AreEqual("11111111222233334444555555555555", OfflineIdentity.Create("中文名", OfflineUuidMode.Custom, "11111111-2222-3333-4444-555555555555"));
        Assert.ThrowsExactly<ArgumentException>(() => OfflineIdentity.Create("Player", OfflineUuidMode.Custom, "00000000-0000-0000-0000-000000000000"));
        Assert.ThrowsExactly<ArgumentException>(() => OfflineIdentity.Create("Player", OfflineUuidMode.Custom, "bad-uuid"));
        Assert.ThrowsExactly<ArgumentException>(() => OfflineIdentity.Create("../name"));
        Assert.AreNotEqual(OfflineIdentity.Create("Steve"), OfflineIdentity.Create("steve"));
    }
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task ExplicitUuidAndChineseNamePersistAcrossRestartAndFortuneIsStable()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No game or network expected.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        var id = "1234567890abcdef1234567890abcdef"; int fortune;
        await using (var service = new LauncherService(platform, http, downloader, new NetworkOptions()))
        {
            var account = await service.AddOfflineAccountAsync("中文角色", OfflineUuidMode.Custom, id);
            Assert.AreEqual(id, account.PlayerUuid); Assert.AreEqual("offline:" + id, account.Id);
            fortune = await service.GetDailyFortuneAsync(new DateOnly(2026, 11, 7)); Assert.IsTrue(fortune is >= 0 and <= 100);
            Assert.AreEqual(fortune, await service.GetDailyFortuneAsync(new DateOnly(2026, 11, 7)));
        }
        await using var restarted = new LauncherService(platform, http, downloader, new NetworkOptions()); await restarted.InitializeAsync();
        Assert.AreEqual(id, restarted.Snapshot.Accounts.Single().PlayerUuid); Assert.AreEqual("中文角色", restarted.Snapshot.Accounts.Single().DisplayName);
        Assert.AreEqual(fortune, await restarted.GetDailyFortuneAsync(new DateOnly(2026, 11, 7)));
    }
    [TestMethod]
    public async Task AnonymousDownloadFollowsExplicitHopsAndCommitsOnlyCompleteNewFile()
    {
        var calls = 0; using var http = new HttpClient(new ScriptedHttpHandler(r =>
        {
            Assert.IsFalse(r.Headers.Contains("Authorization")); Assert.IsFalse(r.Headers.Contains("x-api-key")); Assert.IsFalse(r.Headers.Contains("Cookie")); calls++;
            return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.example.org/file") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        }));
        var destination = Path.Combine(TestFiles.Root(), "中文 文件.bin");
        await ToolFileTransfer.DownloadAsync(http, new("https://download.example.org/start?opaque=fixture"), destination);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination)); Assert.AreEqual(2, calls);
        await Assert.ThrowsAsync<IOException>(() => ToolFileTransfer.DownloadAsync(http, new("https://example.org/retry"), destination));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination)); Assert.AreEqual(2, calls);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.part"));
    }
    [TestMethod]
    public async Task DownloadCancellationAndTruncationRemoveOwnedPartWithoutPublishing()
    {
        var directory = TestFiles.Root(); var destination = Path.Combine(directory, "cancel.bin");
        using var cancelled = new CancellationTokenSource();
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[200000]) })));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ToolFileTransfer.DownloadAsync(http, new("https://example.org/file"), destination, new InlineProgress<TransferProgress>(_ => cancelled.Cancel()), cancelled.Token));
        Assert.IsFalse(File.Exists(destination)); Assert.IsEmpty(Directory.GetFiles(directory));
        using var shortHttp = new HttpClient(new ScriptedHttpHandler(_ =>
        {
            var content = new ByteArrayContent([1, 2]); content.Headers.ContentLength = 50;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolFileTransfer.DownloadAsync(shortHttp, new("https://example.org/file"), destination));
        Assert.IsFalse(File.Exists(destination)); Assert.IsEmpty(Directory.GetFiles(directory));
    }
    [TestMethod]
    public async Task DownloadRejectsTlsDowngradeOversizeCredentialsAndLinkDestination()
    {
        var destination = Path.Combine(TestFiles.Root(), "blocked.bin");
        using var redirect = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.org/file") } })));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolFileTransfer.DownloadAsync(redirect, new("https://example.org/start"), destination));
        Assert.ThrowsExactly<ArgumentException>(() => ToolFileTransfer.ValidateSource(new("https://user:password@example.org/file")));
        using var huge = new HttpClient(new ScriptedHttpHandler(_ => { var content = new ByteArrayContent([1]); content.Headers.ContentLength = ToolFileTransfer.MaximumBytes + 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolFileTransfer.DownloadAsync(huge, new("https://example.org/file"), destination));
        var external = Path.Combine(TestFiles.Root(), "original.bin"); await File.WriteAllTextAsync(external, "keep"); File.CreateSymbolicLink(destination, external);
        await Assert.ThrowsAsync<IOException>(() => ToolFileTransfer.DownloadAsync(huge, new("https://example.org/file"), destination));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(external));
    }
    [TestMethod]
    public async Task CleanupPreviewExcludesUserDataAndRecentFilesAndCanRestoreFromDisk()
    {
        var instance = new GameInstance("fixture", "fixture", "1.21.1", TestFiles.Root());
        var old = await FileAsync(instance, "logs/old.log.gz", "old bytes", true);
        var recent = await FileAsync(instance, "logs/latest.log", "recent", false);
        var world = await FileAsync(instance, "saves/world.dat", "world data", true);
        var unknown = await FileAsync(instance, "logs/user-config.json", "custom config", true);
        var plan = await GameLogCleaner.ScanAsync([instance]); Assert.AreEqual(1, plan.Files.Count);
        var receipts = TestFiles.Root(); await GameLogCleaner.MoveAsync(plan, [instance], receipts);
        Assert.IsFalse(File.Exists(old)); Assert.AreEqual("recent", await File.ReadAllTextAsync(recent)); Assert.AreEqual("world data", await File.ReadAllTextAsync(world)); Assert.AreEqual("custom config", await File.ReadAllTextAsync(unknown));
        await File.WriteAllTextAsync(old, "new conflicting file");
        await Assert.ThrowsAsync<IOException>(() => GameLogCleaner.RestoreLatestAsync([instance], receipts)); Assert.AreEqual("new conflicting file", await File.ReadAllTextAsync(old));
        File.Move(old, old + ".preserved-fixture"); Assert.AreEqual(1, await GameLogCleaner.RestoreLatestAsync([instance], receipts));
        Assert.AreEqual("old bytes", await File.ReadAllTextAsync(old)); Assert.AreEqual(0, await GameLogCleaner.RestoreLatestAsync([instance], receipts));
    }
    [TestMethod]
    public async Task CleanupChangedFileCancellationTraversalAndAliasPreserveSources()
    {
        var instance = new GameInstance("fixture", "fixture", "1.21.1", TestFiles.Root()); var path = await FileAsync(instance, "logs/old.log", "old", true);
        var plan = await GameLogCleaner.ScanAsync([instance]); await File.WriteAllTextAsync(path, "changed");
        await Assert.ThrowsAsync<IOException>(() => GameLogCleaner.MoveAsync(plan, [instance], TestFiles.Root())); Assert.AreEqual("changed", await File.ReadAllTextAsync(path));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameLogCleaner.MoveAsync(plan, [instance], TestFiles.Root(), cancelled.Token));
        var forged = plan with { Files = [plan.Files[0] with { RelativePath = "logs/../saves/level.dat" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => GameLogCleaner.MoveAsync(forged, [instance], TestFiles.Root()));
        Directory.CreateSymbolicLink(Path.Combine(instance.GameDirectory, "crash-reports"), TestFiles.Root());
        await Assert.ThrowsAsync<IOException>(() => GameLogCleaner.ScanAsync([instance]));
    }
    [TestMethod]
    public void AvatarUsesNearestPixelsHatAlphaAndProducesValidCrcPngAtAllSizes()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var y = 8; y < 16; y++) for (var x = 8; x < 16; x++) { var i = (y * 64 + x) * 4; pixels[i] = 100; pixels[i + 3] = 255; }
        var hat = (8 * 64 + 40) * 4; pixels[hat + 1] = 200; pixels[hat + 3] = 128;
        var skin = AccountAppearancePng.Decode(ToolImages.EncodeRgba(64, 64, pixels));
        var avatar = AccountAppearancePng.Decode(ToolImages.Avatar(skin, 64));
        CollectionAssert.AreEqual(new byte[] { 50, 100, 0, 255 }, avatar.Rgba[..4].ToArray());
        CollectionAssert.AreEqual(avatar.Rgba[..4].ToArray(), avatar.Rgba.Slice(7 * 4, 4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 100, 0, 0, 255 }, avatar.Rgba.Slice(8 * 4, 4).ToArray());
        foreach (var size in new[] { 64, 128, 256 })
        { var png = ToolImages.Avatar(skin, size); Assert.AreEqual(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))); Assert.AreEqual(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))); }
        Assert.ThrowsExactly<ArgumentException>(() => ToolImages.Avatar(skin, 1024));
    }
    [TestMethod]
    public async Task PublicSkinUsesAnonymousOfficialEndpointsAndChecksIdentityBeforeTexture()
    {
        const string uuid = "11111111222233334444555555555555";
        var texture = ToolImages.EncodeRgba(64, 64, new byte[64 * 64 * 4]); var requests = new List<Uri>();
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { profileId = uuid, textures = new { SKIN = new { url = "http://textures.minecraft.net/texture/fixture" } } }));
        using var http = new HttpClient(new ScriptedHttpHandler(r =>
        {
            Assert.IsFalse(r.Headers.Contains("Authorization")); Assert.IsFalse(r.Headers.Contains("Cookie")); Assert.IsFalse(r.Headers.Contains("x-api-key")); requests.Add(r.RequestUri!);
            return Task.FromResult(r.RequestUri!.Host switch
            {
                "api.mojang.com" => ScriptedHttpHandler.Json($$"""{"id":"{{uuid}}","name":"Player_1"}"""),
                "sessionserver.mojang.com" => ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { id = uuid, properties = new[] { new { name = "textures", value = encoded } } })),
                "textures.minecraft.net" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(texture) },
                _ => throw new AssertFailedException("Unexpected host.")
            });
        }));
        var skin = await new AccountAppearanceClient(http).ReadPublicMicrosoftSkinAsync("Player_1"); CollectionAssert.AreEqual(texture, skin.Png.ToArray());
        Assert.AreEqual(3, requests.Count); Assert.IsTrue(requests.All(r => r.Scheme == "https"));
        using var wrong = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json($$"""{"id":"{{uuid}}","name":"Other"}"""))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(wrong).ReadPublicMicrosoftSkinAsync("Player_1"));
    }
    private static async Task<string> FileAsync(GameInstance instance, string relative, string text, bool old)
    { var path = Path.Combine(instance.GameDirectory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, text); if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2)); return path; }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
