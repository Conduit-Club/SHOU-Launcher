using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class AccountAppearanceTests
{
    private const string Uuid = "11111111222233334444555555555555";
    private static AccountProfile Offline(string id = "offline:fixture") => new(id, AccountKind.Offline, "外观夹具", Uuid);
    private static AccountProfile Microsoft() => new("microsoft:fixture", AccountKind.Microsoft, "协议夹具", Uuid);
    private static AccountProfile Ygg() => new("yggdrasil:fixture", AccountKind.Yggdrasil, "公开纹理夹具", Uuid, "https://skin.example.org/api/yggdrasil");

    [TestMethod]
    public void RgbaTexturePreservesOriginalPngAndExactPixels()
    {
        var bytes = Png(64, 64, (x, y) => [(byte)(x * 3), (byte)(y * 2), 91, (byte)(x == 0 ? 0 : 255)]);
        var image = AccountAppearancePng.Decode(bytes);
        CollectionAssert.AreEqual(bytes, image.Png.ToArray()); Assert.AreEqual(64, image.Width); Assert.AreEqual(64, image.Height);
        CollectionAssert.AreEqual(new byte[] { 21, 18, 91, 255 }, image.Rgba.Slice((9 * 64 + 7) * 4, 4).ToArray());
        Assert.AreEqual(0, image.Rgba.Span[3]);
    }
    [TestMethod]
    public void IndexedLegacySkinUsesPaletteTransparency()
    {
        var raw = new byte[32 * (1 + 8)]; for (var y = 0; y < 32; y++) for (var i = 1; i < 9; i++) raw[y * 9 + i] = 0x55;
        var image = AccountAppearancePng.Decode(Assemble(64, 32, 1, 3, raw, [("PLTE", new byte[] { 2, 3, 4, 190, 20, 30 }), ("tRNS", new byte[] { 0, 127 })]));
        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 0, 190, 20, 30, 127 }, image.Rgba.Slice(0, 8).ToArray());
    }
    [TestMethod]
    public void CorruptCrcTruncatedDataAndInvalidDimensionsAreRejected()
    {
        var valid = Png(); var corrupt = valid.ToArray(); corrupt[^1] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(corrupt));
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(valid.AsMemory(0, valid.Length - 1)));
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(Png(128, 128)));
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(new byte[AccountAppearancePng.MaximumPngBytes + 1]));
    }
    [TestMethod]
    public void DecompressionBombAndAnimatedPngAreRejectedBeforeRendering()
    {
        var bomb = Assemble(64, 64, 8, 6, new byte[4 * 1024 * 1024]);
        Assert.IsTrue(bomb.Length < 16 * 1024); Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(bomb));
        var animated = Assemble(64, 64, 8, 6, new byte[64 * 257], [("acTL", new byte[8])]);
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(animated));
    }
    [TestMethod]
    public async Task OfflineAppearanceActuallyRoundTripsAcrossNewStoreAndKeepsAccountsIndependent()
    {
        var root = TestFiles.Root(); var bytes = Png(); var one = Offline(); var two = Offline("offline:second");
        var saved = await new AccountAppearanceStore(root).SaveOfflineAsync(one, bytes, AccountSkinModel.Slim);
        var loaded = await new AccountAppearanceStore(root).LoadOfflineAsync(one);
        Assert.AreEqual(AccountSkinModel.Slim, loaded.Model); CollectionAssert.AreEqual(bytes, loaded.Skin!.Png.ToArray());
        Assert.IsNull((await new AccountAppearanceStore(root).LoadOfflineAsync(two)).Skin); Assert.IsTrue(saved.CanSetLocalSkin);
        var record = Directory.GetFiles(Path.Combine(root, "account-appearance")).Single(); var before = await File.ReadAllBytesAsync(record);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceStore(root).SaveOfflineAsync(one, new byte[] { 1, 2, 3 }, AccountSkinModel.Classic));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(record));
    }
    [TestMethod]
    public async Task CancelledLocalSaveAndCorruptRecordDoNotOverwriteOriginal()
    {
        var root = TestFiles.Root(); var store = new AccountAppearanceStore(root); var account = Offline();
        await store.SaveOfflineAsync(account, Png(), AccountSkinModel.Classic); var path = Directory.GetFiles(Path.Combine(root, "account-appearance")).Single(); var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveOfflineAsync(account, Png(), AccountSkinModel.Slim, new CancellationToken(true)));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadOfflineAsync(account)); Assert.AreEqual("{broken", await File.ReadAllTextAsync(path));
    }
    [TestMethod]
    public async Task LinkedAppearanceDirectoryAndPickedFileAreRefused()
    {
        var root = TestFiles.Root(); var external = TestFiles.Root(); var externalSkin = Path.Combine(external, "skin.png"); await File.WriteAllBytesAsync(externalSkin, Png());
        Directory.CreateSymbolicLink(Path.Combine(root, "account-appearance"), external);
        await Assert.ThrowsAsync<IOException>(() => new AccountAppearanceStore(root).SaveOfflineAsync(Offline(), Png(), AccountSkinModel.Classic));
        Assert.AreEqual(1, Directory.GetFiles(external).Length);
        var picked = Path.Combine(root, "picked.png"); File.CreateSymbolicLink(picked, externalSkin);
        await Assert.ThrowsAsync<IOException>(() => AccountAppearanceStore.ReadPickedSkinAsync(picked));
    }
    [TestMethod]
    public async Task MicrosoftProfileReadsRealSkinAndAllCapesButSendsBearerOnlyToOfficialProfile()
    {
        var seen = new List<(string Uri, string? Authorization)>(); var skin = Png(); var cape = Png(64, 32);
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            seen.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            if (request.RequestUri.Host == "api.minecraftservices.com") return Task.FromResult(ScriptedHttpHandler.Json($$$$"""{"id":"{{{{Uuid}}}}","skins":[{"state":"ACTIVE","variant":"SLIM","url":"http://textures.minecraft.net/texture/skin"}],"capes":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","state":"ACTIVE","alias":"夹具披风","url":"https://textures.minecraft.net/texture/cape"}]}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("skin") ? skin : cape) });
        }));
        var appearance = await new AccountAppearanceClient(http).ReadMicrosoftAsync(Microsoft(), "fixture-token");
        Assert.AreEqual(AccountSkinModel.Slim, appearance.Model); Assert.IsNotNull(appearance.Skin); Assert.AreEqual(1, appearance.Capes.Count); Assert.AreEqual("夹具披风", appearance.Capes[0].Name); Assert.IsTrue(appearance.Capes[0].IsActive);
        Assert.AreEqual("Bearer fixture-token", seen[0].Authorization); Assert.IsTrue(seen.Skip(1).All(s => s.Authorization is null && s.Uri.StartsWith("https://textures.minecraft.net/", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task ProfileMismatchAndUnapprovedTextureSourceStopBeforeImageRequest()
    {
        var count = 0; var id = new string('a', 32);
        using var http = new HttpClient(new ScriptedHttpHandler(_ => { count++; return Task.FromResult(ScriptedHttpHandler.Json($$$$"""{"id":"{{{{id}}}}","skins":[{"state":"ACTIVE","variant":"CLASSIC","url":"https://attacker.invalid/skin.png"}],"capes":[]}""")); }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(http).ReadMicrosoftAsync(Microsoft(), "fixture-token")); Assert.AreEqual(1, count);
        id = Uuid;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(http).ReadMicrosoftAsync(Microsoft(), "fixture-token")); Assert.AreEqual(2, count);
    }
    [TestMethod]
    public async Task YggdrasilPublicMetadataAndProfileNeverReceiveTokensAndRespectDomainRules()
    {
        var requests = new List<Uri>(); var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$$$"""{"profileId":"{{{{Uuid}}}}","textures":{"SKIN":{"url":"https://cdn.example.org/skin.png","metadata":{"model":"slim"}},"CAPE":{"url":"https://cdn.example.org/cape.png"}}}"""));
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            requests.Add(request.RequestUri!); Assert.IsNull(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/yggdrasil/")) return Task.FromResult(ScriptedHttpHandler.Json("{\"skinDomains\":[\"cdn.example.org\"]}"));
            if (request.RequestUri.AbsolutePath.Contains("/profile/")) return Task.FromResult(ScriptedHttpHandler.Json($$$$"""{"id":"{{{{Uuid}}}}","properties":[{"name":"textures","value":"{{{{payload}}}}"}]}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png(64, request.RequestUri.AbsolutePath.Contains("cape") ? 32 : 64)) });
        }));
        var result = await new AccountAppearanceClient(http).ReadYggdrasilAsync(Ygg());
        Assert.IsNotNull(result.Skin); Assert.AreEqual(1, result.Capes.Count); Assert.AreEqual(4, requests.Count);
        Assert.AreEqual("https://skin.example.org/api/yggdrasil/sessionserver/session/minecraft/profile/" + Uuid + "?unsigned=true", requests[1].AbsoluteUri);
        Assert.IsFalse(result.CanSetLocalSkin); Assert.AreEqual("https://skin.example.org/", result.ManagementUri!.AbsoluteUri);
    }
    [TestMethod]
    public async Task YggdrasilRejectsMalformedBase64AndLookalikeTextureHost()
    {
        var payload = "not-base64"; var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            calls++; return Task.FromResult(ScriptedHttpHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("/api/yggdrasil/") ? "{\"skinDomains\":[\".example.org\"]}" : $$$$"""{"id":"{{{{Uuid}}}}","properties":[{"name":"textures","value":"{{{{payload}}}}"}]}"""));
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(http).ReadYggdrasilAsync(Ygg()));
        payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$$$"""{"profileId":"{{{{Uuid}}}}","textures":{"SKIN":{"url":"https://example.org.attacker.invalid/skin.png"}}}"""));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(http).ReadYggdrasilAsync(Ygg())); Assert.AreEqual(4, calls);
    }
    [TestMethod]
    public async Task RedirectAndUnboundedChunkedBodyAreRefusedWithoutEchoingSecrets()
    {
        using var redirect = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://attacker.invalid/") } })));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(redirect).ReadMicrosoftAsync(Microsoft(), "fixture-secret")); Assert.IsFalse(error.Message.Contains("fixture-secret", StringComparison.Ordinal));
        using var huge = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[300 * 1024])) })));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new AccountAppearanceClient(huge).ReadMicrosoftAsync(Microsoft(), "fixture-secret"));
    }
    [TestMethod]
    public async Task ServiceChecksCapturedOfflineAccountAndActuallyPersistsOnlySelectedTarget()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Offline appearance must not access a network."))); var downloader = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var processes = (ProcessRunner)platform.Processes; await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        await service.InitializeAsync(); var first = await service.AddOfflineAccountAsync("FirstFixture"); var second = await service.AddOfflineAccountAsync("SecondFixture");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SaveOfflineAccountSkinAsync(first.Id, Png(), AccountSkinModel.Slim));
        Assert.IsNull((await service.GetAccountAppearanceAsync(first.Id)).Skin); Assert.IsNull((await service.GetAccountAppearanceAsync(second.Id)).Skin);
        await service.SaveOfflineAccountSkinAsync(second.Id, Png(), AccountSkinModel.Slim);
        Assert.AreEqual(AccountSkinModel.Slim, (await service.GetAccountAppearanceAsync(second.Id)).Model); Assert.IsNull((await service.GetAccountAppearanceAsync(first.Id)).Skin);
    }
    // A tiny independent fixture encoder: fixed filter 0, zlib, critical chunks. Pixel assertions are above.
    private static byte[] Png(int width = 64, int height = 64, Func<int, int, byte[]>? pixel = null)
    {
        pixel ??= (x, y) => [(byte)(x * 3), (byte)(y * 2), 91, 255]; using var raw = new MemoryStream();
        for (var y = 0; y < height; y++) { raw.WriteByte(0); for (var x = 0; x < width; x++) raw.Write(pixel(x, y)); }
        return Assemble(width, height, 8, 6, raw.ToArray());
    }
    private static byte[] Assemble(int width, int height, byte depth, byte color, byte[] raw, (string Tag, byte[] Bytes)[]? extras = null)
    {
        using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]); var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = depth; header[9] = color; Chunk(output, "IHDR", header);
        foreach (var (tag, bytes) in extras ?? []) Chunk(output, tag, bytes);
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []); return output.ToArray();
    }
    private static void Chunk(Stream output, string tag, byte[] bytes)
    {
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); output.Write(length); var content = Encoding.ASCII.GetBytes(tag).Concat(bytes).ToArray(); output.Write(content);
        var value = 0xffffffffu; foreach (var b in content) { value ^= b; for (var bit = 0; bit < 8; bit++) value = value >> 1 ^ ((value & 1) * 0xedb88320u); } BinaryPrimitives.WriteUInt32BigEndian(length, ~value); output.Write(length);
    }
}
