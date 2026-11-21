using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class AccountAppearanceTests
{
    private const string CapeOne = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", CapeTwo = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
    [TestMethod]
    public async Task NativePickerSkinExportAtomicallyReplacesAndCancelBadInputOrLinksKeepTarget()
    {
        var root=TestFiles.Root(); var path=Path.Combine(root,"皮肤 with spaces.png"); var before=Png(); var after=Png(64,64,(_,_)=>[12,34,56,255]); await File.WriteAllBytesAsync(path,before);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>AccountSkinExport.SaveAsync(path,after,new CancellationToken(true)));
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(path));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>AccountSkinExport.SaveAsync(path,new byte[]{1,2}));
        await AccountSkinExport.SaveAsync(path,after); CollectionAssert.AreEqual(after,await File.ReadAllBytesAsync(path));
        var link=Path.Combine(root,"link.png"); File.CreateSymbolicLink(link,path);
        await Assert.ThrowsAsync<IOException>(()=>AccountSkinExport.SaveAsync(link,before)); CollectionAssert.AreEqual(after,await File.ReadAllBytesAsync(path)); Assert.IsFalse(Directory.GetFiles(root,"*.tmp").Any());
    }
    private sealed class AppearanceServer : HttpMessageHandler
    {
        public byte[] Skin = Png();
        public AccountSkinModel Model = AccountSkinModel.Classic;
        public string? ActiveCape = CapeOne;
        public int Writes, ProfileReads;
        public bool BackedUp, FailReadback, LoseWriteResponse;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? RequestBody;
        public byte[]? Uploaded;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "textures.minecraft.net")
            {
                Assert.IsNull(request.Headers.Authorization);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("skin") ? Skin : Png(64, 32)) };
            }
            Assert.AreEqual("api.minecraftservices.com", request.RequestUri.Host);
            Assert.AreEqual("Bearer fixture-token", request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                ProfileReads++;
                if (Writes > 0 && FailReadback) throw new HttpRequestException("synthetic sensitive body must never appear");
                return ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { id = Uuid,
                    skins = new[] { new { state = "ACTIVE", variant = Model == AccountSkinModel.Slim ? "SLIM" : "CLASSIC", url = "https://textures.minecraft.net/texture/skin" } },
                    capes = new[] { CapeOne, CapeTwo }.Select(id => new { id, state = ActiveCape == id ? "ACTIVE" : "INACTIVE", alias = id, url = "https://textures.minecraft.net/texture/cape" }) }));
            }
            Assert.IsTrue(BackedUp, "Mutation was sent before backup verification"); Writes++;
            if (LoseWriteResponse) throw new OperationCanceledException("synthetic private transport detail");
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("synthetic sensitive response") };
            if (request.Method == HttpMethod.Post)
            {
                Assert.AreEqual("/minecraft/profile/skins", request.RequestUri.AbsolutePath);
                var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                Assert.AreEqual(2, parts.Length); Assert.AreEqual("image/png", parts[1].Headers.ContentType?.MediaType);
                Assert.AreEqual("skin.png", parts[1].Headers.ContentDisposition?.FileName?.Trim('"'));
                Model = await parts[0].ReadAsStringAsync(ct) == "slim" ? AccountSkinModel.Slim : AccountSkinModel.Classic;
                Uploaded = Skin = await parts[1].ReadAsByteArrayAsync(ct);
            }
            else
            {
                Assert.AreEqual("/minecraft/profile/capes/active", request.RequestUri.AbsolutePath);
                if (request.Method == HttpMethod.Put)
                { RequestBody = await request.Content!.ReadAsStringAsync(ct); ActiveCape = JsonDocument.Parse(RequestBody).RootElement.GetProperty("capeId").GetString(); }
                else { Assert.AreEqual(HttpMethod.Delete, request.Method); Assert.IsNull(request.Content); ActiveCape = null; }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
    [TestMethod]
    public async Task OnlineSkinActuallyUploadsExactPngAfterDurableBackupAndReadsBackModelAndPixels()
    {
        var root = TestFiles.Root(); var server = new AppearanceServer(); using var http = new HttpClient(server);
        var png = Png(64, 64, (_, _) => [200, 50, 90, 255]); string? backup = null;
        var result = await new AccountAppearanceClient(http).ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.UploadSkin, png, AccountSkinModel.Slim), async (before, ct) =>
        { backup = await new AccountAppearanceStore(root).BackupOnlineAsync(Microsoft(), before, ct); server.BackedUp = File.Exists(backup); });
        Assert.AreEqual(AccountAppearanceWriteState.Verified, result.State); Assert.AreEqual(1, server.Writes);
        CollectionAssert.AreEqual(png, server.Uploaded!); CollectionAssert.AreEqual(png, result.Appearance!.Skin!.Png.ToArray()); Assert.AreEqual(AccountSkinModel.Slim, result.Appearance.Model);
        var backupText = await File.ReadAllTextAsync(backup!); Assert.IsFalse(backupText.Contains("fixture-token"));
        using var record = JsonDocument.Parse(backupText);
        CollectionAssert.AreEqual(Png(), record.RootElement.GetProperty("Png").GetBytesFromBase64());
        Assert.AreEqual(CapeOne, record.RootElement.GetProperty("ActiveCapeId").GetString());
    }
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task OwnedCapeWearAndHideAreVerifiedAgainstFreshOfficialProfile(bool hide)
    {
        var server = new AppearanceServer { BackedUp = true }; using var http = new HttpClient(server);
        var result = await new AccountAppearanceClient(http).ChangeMicrosoftAsync(Microsoft(), "fixture-token", hide ? new(AccountAppearanceAction.HideCape) : new(AccountAppearanceAction.WearCape, CapeId: CapeTwo), (_, _) => Task.CompletedTask);
        Assert.AreEqual(AccountAppearanceWriteState.Verified, result.State); Assert.AreEqual(1, server.Writes);
        Assert.AreEqual(hide ? null : CapeTwo, result.Appearance!.Capes.FirstOrDefault(c => c.IsActive)?.Id);
    }
    [TestMethod]
    public async Task UnknownCapeAndFailedBackupNeverSendWrites()
    {
        var server = new AppearanceServer(); using var http = new HttpClient(server); var client = new AccountAppearanceClient(http);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.WearCape, CapeId: Guid.NewGuid().ToString()), (_, _) => throw new AssertFailedException()));
        await Assert.ThrowsExactlyAsync<IOException>(() => client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.HideCape), (_, _) => throw new IOException("synthetic backup refusal")));
        Assert.AreEqual(0, server.Writes);
    }
    [TestMethod]
    public async Task AlreadySelectedSkinOrCapeDoesNotWriteOrCreateRedundantBackup()
    {
        var server = new AppearanceServer(); using var http = new HttpClient(server); var client = new AccountAppearanceClient(http);
        var one = await client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.UploadSkin, Png()), (_, _) => throw new AssertFailedException());
        var two = await client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.WearCape, CapeId: CapeOne), (_, _) => throw new AssertFailedException());
        Assert.AreEqual(AccountAppearanceWriteState.Verified, one.State); Assert.AreEqual(AccountAppearanceWriteState.Verified, two.State); Assert.AreEqual(0, server.Writes);
    }
    [TestMethod]
    [DataRow(401)] [DataRow(429)] [DataRow(500)] [DataRow(302)]
    public async Task FailedMutationDoesNotEchoRemoteResponseOrPretendSuccess(int status)
    {
        var server = new AppearanceServer { BackedUp = true, Status = (HttpStatusCode)status }; using var http = new HttpClient(server);
        var result = await new AccountAppearanceClient(http).ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.HideCape), (_, _) => Task.CompletedTask);
        Assert.AreEqual(status == 302 || status >= 500 ? AccountAppearanceWriteState.OutcomeUnknown : AccountAppearanceWriteState.Rejected, result.State);
        Assert.IsNull(result.Appearance); Assert.IsFalse(result.Message.Contains("sensitive")); Assert.AreEqual(1, server.Writes);
    }
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LostResponseOrFailedReadbackRequireExplicitRefreshWithoutAutomaticRetry(bool accepted)
    {
        var server = new AppearanceServer { BackedUp = true, LoseWriteResponse = !accepted, FailReadback = accepted }; using var http = new HttpClient(server);
        var result = await new AccountAppearanceClient(http).ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.HideCape), (_, _) => Task.CompletedTask);
        Assert.AreEqual(accepted ? AccountAppearanceWriteState.AcceptedNeedsRefresh : AccountAppearanceWriteState.OutcomeUnknown, result.State);
        Assert.IsNull(result.Appearance); Assert.AreEqual(1, server.Writes); Assert.IsFalse(result.Message.Contains("synthetic"));
    }
    [TestMethod]
    public async Task CancelBeforeSendAndInvalidModelOrProviderDoNotMutateOrTruncateBackups()
    {
        var root = TestFiles.Root(); var server = new AppearanceServer(); using var http = new HttpClient(server); var client = new AccountAppearanceClient(http);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.HideCape), (_, _) => Task.CompletedTask, new CancellationToken(true)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ChangeMicrosoftAsync(Microsoft(), "fixture-token", new(AccountAppearanceAction.UploadSkin, Png(64,32), AccountSkinModel.Slim), (_, _) => Task.CompletedTask));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ChangeMicrosoftAsync(Ygg(), "fixture-token", new(AccountAppearanceAction.HideCape), (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new AccountAppearanceStore(root).BackupOnlineAsync(Microsoft(), new(Microsoft().Id, AccountSkinModel.Classic, AccountAppearancePng.Decode(Png()), [], false, null, ""), new CancellationToken(true)));
        Assert.AreEqual(0, server.Writes); Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    }
}
