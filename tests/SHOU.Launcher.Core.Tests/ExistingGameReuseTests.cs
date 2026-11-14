using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ExistingGameReuseTests
{
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
    private static void Put(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
    private sealed class Progress(List<TransferProgress> values, Action<TransferProgress>? action = null) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) { values.Add(value); action?.Invoke(value); } }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task VerifiedOldFileAvoidsHttpAndAria2AndCountsAsCacheReuse(bool aria2)
    {
        var root=TestFiles.Root();var old=Path.Combine(root,"old");var source=Path.Combine(old,"libraries/fixture.jar");var target=Path.Combine(root,"cache/library.jar");
        var bytes="verified existing file"u8.ToArray();Put(source,bytes);var calls=0;var warnings=0;
        using var http=new HttpClient(new ScriptedHttpHandler(_=>{calls++;throw new AssertFailedException("Existing verified bytes must not use a network.");}));
        var options=new NetworkOptions();options.Update(new(Proxy:new(ProxyMode.Direct),Aria2:new(aria2,"http://127.0.0.1:1/jsonrpc")));
        var downloader=new DownloadCoordinator(http,new VerifiedDownloader(http,options),options,_=>warnings++);
        var progress=new List<TransferProgress>();
        await downloader.DownloadAsync(new([new("https://example.invalid/library.jar")],target,Hash(bytes),Size:bytes.Length,
            LocalSources:[new(old,"libraries/fixture.jar")]),new Progress(progress));
        Assert.AreEqual(0,calls);Assert.AreEqual(0,warnings);CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(target));CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(source));
        var done=progress.Last();Assert.AreEqual("Completed",done.Stage);Assert.AreEqual("cache",done.Source);Assert.AreEqual(bytes.Length,done.BytesReceived);
        Assert.IsFalse(Directory.GetFiles(Path.GetDirectoryName(target)!).Any(p=>p.Contains(".reuse-")));
    }

    [TestMethod, DataRow("wrong-hash"),DataRow("missing"),DataRow("escape"),DataRow("unhashed")]
    public async Task MissingCorruptOrUnauthorizedOldFilesUseVerifiedNetworkWithoutChangingOriginal(string scenario)
    {
        var root=TestFiles.Root();var old=Path.Combine(root,"old");Directory.CreateDirectory(old);var source=Path.Combine(old,"file.bin");var target=Path.Combine(root,"cache/file.bin");
        var bytes="expected"u8.ToArray();var bad="original"u8.ToArray();Put(source,bad);Put(Path.Combine(root,"private.bin"),bad);var calls=0;
        using var http=new HttpClient(new ScriptedHttpHandler(_=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}));
        var relative=scenario switch{"missing"=>"missing.bin","escape"=>"../private.bin",_=>"file.bin"};
        await new VerifiedDownloader(http).DownloadAsync(new([new("https://example.invalid/file")],target,scenario=="unhashed"?null:Hash(bytes),Size:bytes.Length,LocalSources:[new(old,relative)]));
        Assert.AreEqual(1,calls);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(target));CollectionAssert.AreEqual(bad,File.ReadAllBytes(source));CollectionAssert.AreEqual(bad,File.ReadAllBytes(Path.Combine(root,"private.bin")));
        Assert.IsFalse(Directory.GetFiles(Path.GetDirectoryName(target)!).Any(p=>p.Contains(".reuse-")));
    }

    [TestMethod]
    public async Task CancelledLocalReuseRetainsExistingDestinationAndOriginal()
    {
        var root=TestFiles.Root();var old=Path.Combine(root,"old");var source=Path.Combine(old,"source.bin");var target=Path.Combine(root,"cache/file.bin");
        var bytes="expected"u8.ToArray();var original="keep me"u8.ToArray();Put(source,bytes);Put(target,original);
        using var http=new HttpClient(new ScriptedHttpHandler(_=>throw new AssertFailedException("Cancelled copy must not start transport.")));
        using var cancel=new CancellationTokenSource();var progress=new Progress([],p=>{if(p.Stage=="Verifying")cancel.Cancel();});
        await Assert.ThrowsAsync<OperationCanceledException>(()=>new VerifiedDownloader(http).DownloadAsync(new([new("https://example.invalid/file")],target,Hash(bytes),Size:bytes.Length,LocalSources:[new(old,"source.bin")]),progress,cancel.Token));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(target));CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source));
        Assert.IsFalse(Directory.GetFiles(Path.GetDirectoryName(target)!).Any(p=>p.Contains(".reuse-")));
    }

    [TestMethod]
    public async Task LinkedOldSourceIsNotReadAndUnrelatedTargetIsPreserved()
    {
        var root = TestFiles.Root(); var old = Path.Combine(root, "old"); Directory.CreateDirectory(old);
        var outside = Path.Combine(root, "outside.bin"); var target = Path.Combine(root, "new/file.bin");
        var bytes = "expected"u8.ToArray(); var original = "preserve"u8.ToArray(); Put(outside, original);
        File.CreateSymbolicLink(Path.Combine(old, "linked.bin"), outside); var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
        await new VerifiedDownloader(http).DownloadAsync(new([new("https://example.invalid/file")], target, Hash(bytes), Size: bytes.Length, LocalSources: [new(old, "linked.bin")]));
        Assert.AreEqual(1, calls); CollectionAssert.AreEqual(original, File.ReadAllBytes(outside)); CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task AttachedProfileReusesOriginalClientLibraryIndexAndAssetWithAnEmptyNewCache()
    {
        var root=TestFiles.Root();var old=Path.Combine(root,"old .minecraft");var versionFolder=Path.Combine(old,"versions/Friendly profile 中文");
        byte[] client="client"u8.ToArray(),library="library"u8.ToArray(),asset="texture"u8.ToArray();
        var index=Encoding.UTF8.GetBytes(new JsonObject{["objects"]=new JsonObject{["fixture/texture"] = new JsonObject{["hash"]=Hash(asset),["size"]=asset.Length}}}.ToJsonString());
        Put(Path.Combine(versionFolder,"Friendly profile 中文.jar"),client);Put(Path.Combine(old,"libraries/fixture/library/1/library-1.jar"),library);
        Put(Path.Combine(old,"assets/indexes/fixture.json"),index);Put(Path.Combine(old,"assets/objects/"+Hash(asset)[..2]+"/"+Hash(asset)),asset);
        var metadata=new JsonObject{["id"]="local-fixture",["jar"]="local-fixture",["mainClass"]="net.minecraft.client.main.Main",
            ["downloads"]=new JsonObject{["client"]=Descriptor(client,"client")},["assetIndex"]=Descriptor(index,"index"),["libraries"]=new JsonArray(new JsonObject{
                ["name"]="fixture:library:1",["downloads"]=new JsonObject{["artifact"]=Descriptor(library,"library")}})};
        metadata["assetIndex"]!["id"]="fixture";metadata["libraries"]![0]!["downloads"]!["artifact"]!["path"]="fixture/library/1/library-1.jar";
        var version=new ResolvedVersion("local-fixture",metadata,Path.Combine(versionFolder,"Friendly profile 中文.json"),["1.21.1","local-fixture"]);
        var progress=new List<TransferProgress>();using var http=new HttpClient(new ScriptedHttpHandler(_=>throw new AssertFailedException("All four game files already exist and must be reused.")));
        var result=await new MinecraftInstaller(http,new VerifiedDownloader(http),Path.Combine(root,"new cache"),()=>PCL.Core.Conduit.DownloadSources.Defaults()).InstallAsync(version,new("windows","10",RuntimeArchitecture.X64),Path.Combine(root,"new game"),new Progress(progress),
            existingOrigin:new(old,versionFolder,"Friendly profile 中文",new string('a',64)));
        CollectionAssert.AreEqual(client,File.ReadAllBytes(result.ClientJar));Assert.AreEqual(4,progress.Count(p=>p.Stage=="Completed"&&p.Source=="cache"));
        Assert.AreEqual(client.Length+library.Length+index.Length+asset.Length,progress.Where(p=>p.Stage=="Completed").Sum(p=>p.BytesReceived));
        CollectionAssert.AreEqual(client,File.ReadAllBytes(Path.Combine(versionFolder,"Friendly profile 中文.jar")));
    }
    private static JsonObject Descriptor(byte[] bytes,string name)=>new(){["sha1"]=Hash(bytes),["size"]=(long)bytes.Length,["url"]="https://example.invalid/"+name};
}
