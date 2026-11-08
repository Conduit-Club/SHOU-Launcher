using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LegacyInstanceManagementTests
{
    internal static string Profile(string root, string name, string? coordinate = null, string? parent = null)
    {
        var folder = Path.Combine(root, "versions", name); Directory.CreateDirectory(folder);
        var json = new JsonObject { ["id"] = name, ["clientVersion"] = "1.21.1", ["mainClass"] = "net.minecraft.client.main.Main", ["type"] = "release", ["libraries"] = new JsonArray() };
        if (coordinate is not null) json["libraries"] = new JsonArray(new JsonObject { ["name"] = coordinate });
        if (parent is not null) json["inheritsFrom"] = parent;
        File.WriteAllText(Path.Combine(folder, name + ".json"), json.ToJsonString()); return folder;
    }
    [TestMethod, DataRow("forge", "--fml.forgeVersion", "47.3.7"), DataRow("neoforge", "--fml.neoForgeVersion", "21.1.213")]
    public async Task ModernFmlIdentityUsesExplicitPublicFlagsWithoutGuessingFromDirectoryOrIcon(string loader, string flag, string version)
    {
        var root = TestFiles.Root(); var folder = Profile(root, "friendly pack"); var path = Path.Combine(folder, "friendly pack.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher"; json["arguments"] = new JsonObject { ["game"] = new JsonArray(flag, version, "--fml.mcVersion", "1.21.1", "--launchTarget", "forgeclient") };
        await File.WriteAllTextAsync(path, json.ToJsonString()); var row = (await ExistingInstanceReader.InspectAsync(root)).Single(); Assert.AreEqual(loader, row.Loader); Assert.AreEqual(version, row.LoaderVersion); Assert.AreEqual(folder, row.GameDirectory);
        json["arguments"]!["game"] = new JsonArray("--launchTarget", "forgeclient"); await File.WriteAllTextAsync(path, json.ToJsonString()); var unknown = await ExistingInstanceReader.InspectDetailedAsync(root); Assert.IsEmpty(unknown.Candidates); Assert.HasCount(1, unknown.Issues);
    }
    [TestMethod]
    public async Task EqualsDelimitedLegacyLogoKeepsUriColonsInsideValue()
    {
        var root = TestFiles.Root(); var folder = Profile(root, "logo"); Directory.CreateDirectory(Path.Combine(folder, "PCL"));
        await File.WriteAllTextAsync(Path.Combine(folder, "PCL/Setup.ini"), "Logo=pack://application:,,,/old;component/Images/Blocks/GoldBlock.png\nCustomInfo=caption: keep\n");
        var row = (await ExistingInstanceReader.InspectAsync(root)).Single(); Assert.AreEqual("goldblock", row.Presentation!.Icon); Assert.AreEqual("caption: keep", row.Presentation.Description);
    }
    [TestMethod]
    public void LegacyApngIconUsesOnlyFullDefaultCanvasAndDoesNotRelaxSkinValidation()
    {
        var png = Solid(64, 64, 12, 34, 56); var animation = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(animation, 1);
        var frame = new byte[26]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 64); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), 64);
        byte[] Animated(byte[] f) => [..png[..33], ..Chunk("acTL", animation), ..Chunk("fcTL", f), ..png[33..]];
        var animated = Animated(frame); var icon = AccountAppearancePng.DecodeIcon(InstanceIconImages.Normalize(animated)); Assert.AreEqual(256, icon.Width); Assert.AreEqual((byte)34, icon.Rgba.Span[1]);
        Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.Decode(animated)); frame[7] = 32; Assert.ThrowsExactly<InvalidDataException>(() => AccountAppearancePng.DecodeIcon(Animated(frame)));
    }
    private static byte[] Chunk(string tag, byte[] body)
    {
        var bytes = new byte[body.Length + 12]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)body.Length); System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 4); body.CopyTo(bytes, 8);
        var crc = uint.MaxValue; foreach (var b in bytes.AsSpan(4, body.Length + 4)) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : crc >> 1 ^ 0xedb88320u; }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(body.Length + 8), ~crc); return bytes;
    }
    [TestMethod]
    public async Task WholeFolderKeepsHealthyProfilesAndReportsBrokenOneWithoutTouchingSource()
    {
        var root = TestFiles.Root(); Profile(root, "1.21.1"); var folder = Profile(root, "中文 Fabric 长名称", "net.fabricmc:fabric-loader:0.19.3");
        Directory.CreateDirectory(Path.Combine(folder, "PCL")); await File.WriteAllTextAsync(Path.Combine(folder, "PCL/Setup.ini"), "IsStar:True\nCustomInfo:已保存的描述\nVersionLaunchCount:17\nLogo:pack://application:,,,/old;component/Images/Blocks/RedstoneLampOn.png\nUnknownFuture:keep\n");
        Profile(root, "继承子档", parent: "中文 Fabric 长名称"); var broken = Profile(root, "损坏档"); await File.WriteAllTextAsync(Path.Combine(broken, "损坏档.json"), "{broken");
        var before = Files(root); var result = await ExistingInstanceReader.InspectDetailedAsync(root);
        Assert.HasCount(3, result.Candidates); Assert.HasCount(1, result.Issues); Assert.AreEqual("损坏档", result.Issues[0].Name);
        var candidate = result.Candidates.Single(c => c.Name == "中文 Fabric 长名称"); Assert.AreEqual("fabric", candidate.Loader); Assert.AreEqual("0.19.3", candidate.LoaderVersion); Assert.AreEqual(folder, candidate.GameDirectory);
        Assert.AreEqual(new InstancePresentation("redstonelamp", "auto", "已保存的描述", true, 17), candidate.Presentation);
        Assert.AreEqual("fabric", result.Candidates.Single(c => c.Name == "继承子档").Loader);
        CollectionAssert.AreEqual(before, Files(root));
    }
    [TestMethod, DataRow("optifine:OptiFine:1.21.1_J1", "optifine"), DataRow("com.cleanroommc:cleanroom:0.7.14-alpha", "cleanroom"), DataRow("net.neoforged:neoforge:21.1.265", "neoforge"), DataRow("org.quiltmc:quilt-loader:0.30.2", "quilt"), DataRow("com.mumfrey:liteloader:1.7.10", "liteloader"), DataRow("net.legacyfabric:fabric-loader:0.20.1", "legacyfabric"), DataRow("net.labymod:labymod:49", "labymod")]
    public async Task ExistingProfilesRecognizePortableLoaderCoordinates(string coordinate, string loader)
    { var root = TestFiles.Root(); var folder = Profile(root, "原有实例 中文", coordinate); var candidate = (await ExistingInstanceReader.InspectAsync(folder)).Single(); Assert.AreEqual(loader, candidate.Loader); Assert.AreEqual("1.21.1", candidate.GameVersion); Assert.AreEqual(folder, candidate.GameDirectory); }
    [TestMethod]
    public async Task IdenticalJsonInDifferentVersionDirectoriesDoesNotCollapseIdentityAndStandaloneStaysWithinSelectedFolder()
    {
        var root = TestFiles.Root(); var a = Profile(root, "one"); var b = Profile(root, "two"); File.Copy(Path.Combine(a, "one.json"), Path.Combine(b, "two.json"), true);
        var candidates = await ExistingInstanceReader.InspectAsync(root); Assert.HasCount(2, candidates.Select(c => c.VersionId).Distinct().ToArray());
        var standalone = Path.Combine(TestFiles.Root(), "chosen"); Directory.CreateDirectory(standalone); File.Copy(Path.Combine(a, "one.json"), Path.Combine(standalone, "chosen.json"));
        var only = (await ExistingInstanceReader.InspectAsync(standalone)).Single(); Assert.AreEqual(standalone, only.GameDirectory); Assert.AreEqual(standalone, only.Origin.MinecraftRoot);
    }
    [TestMethod]
    public async Task BatchAttachPersistsMetadataSelectionAndBackupsAcrossRestartWithoutChangingLegacyFiles()
    {
        var root = TestFiles.Root(); Profile(root, "plain"); var folder = Profile(root, "Fabric 中文 空格", "net.fabricmc:fabric-loader:0.19.3");
        Directory.CreateDirectory(Path.Combine(folder, "PCL")); File.WriteAllText(Path.Combine(folder, "PCL/Setup.ini"), "IsStar:True\nCustomInfo:legacy\n"); var before = Files(root);
        await using var f = await Fixture.Create(); var added = await f.Service.AttachExistingInstancesAsync((await f.Service.InspectInstanceFolderDetailedAsync(root)).Candidates);
        Assert.HasCount(2, added); Assert.AreEqual(added[0].Id, f.Service.Snapshot.SelectedInstanceId); var favorite = added.Single(i => i.Name == "Fabric 中文 空格");
        await f.Service.UpdateInstancePresentationAsync(favorite.Id, new("forge", "测试", "保存后的描述", true));
        Assert.IsTrue(File.Exists(f.Config + ".bak"));
        await using var store = new Configuration.ConfigurationStore(f.Platform.Paths); var reloaded = await store.LoadAsync();
        Assert.HasCount(2, reloaded.Instances); Assert.AreEqual("保存后的描述", reloaded.Instances.Single(i => i.Id == favorite.Id).Presentation!.Description);
        CollectionAssert.AreEqual(before, Files(root)); Assert.AreEqual(0, f.Guard.Calls);
    }
    [TestMethod, DataRow("changed"), DataRow("duplicate"), DataRow("cancel"), DataRow("save-failure")]
    public async Task InterruptedOrChangedBatchDoesNotPartiallyRegisterOrMutateSource(string scenario)
    {
        var root = TestFiles.Root(); Profile(root, "one"); var folder = Profile(root, "two"); await using var f = await Fixture.Create(); var candidates = (await f.Service.InspectInstanceFolderDetailedAsync(root)).Candidates.ToArray();
        if (scenario == "changed") await File.AppendAllTextAsync(Path.Combine(folder, "two.json"), " ");
        if (scenario == "duplicate") candidates = [candidates[0], candidates[0]];
        var before = Files(root); var stateBefore = await File.ReadAllBytesAsync(f.Config); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "save-failure") { File.Move(f.Config, f.Config + ".held"); Directory.CreateDirectory(f.Config); }
        try { await Reject(() => f.Service.AttachExistingInstancesAsync(candidates, cts.Token)); Assert.IsEmpty(f.Service.Snapshot.Instances); CollectionAssert.AreEqual(before, Files(root)); }
        finally { if (scenario == "save-failure") { Directory.Delete(f.Config); File.Move(f.Config + ".held", f.Config); } }
        CollectionAssert.AreEqual(stateBefore, await File.ReadAllBytesAsync(f.Config));
    }
    [TestMethod]
    public async Task CustomSquareIconPersistsAndAutoRestoresLegacyIconWithoutDeletingBackups()
    {
        var root = TestFiles.Root(); var folder = Profile(root, "整合包图标", "net.fabricmc:fabric-loader:0.19.3"); Directory.CreateDirectory(Path.Combine(folder, "PCL"));
        var legacy = Solid(16, 16, 220, 20, 10); await File.WriteAllBytesAsync(Path.Combine(folder, "PCL/Logo.png"), legacy);
        await using var f = await Fixture.Create(); var instance = await f.Service.AttachExistingInstanceAsync((await ExistingInstanceReader.InspectAsync(folder)).Single()); var before = Files(root);
        var old = await f.Service.ReadInstanceIconAsync(instance.Id); Assert.AreEqual(256, AccountAppearancePng.DecodeIcon(old!).Width);
        var custom = Solid(256, 256, 10, 20, 230); var save = f.Service.SaveInstanceIconAsync(instance.Id, custom); Array.Fill(custom, (byte)0); await save;
        var image = await f.Service.ReadInstanceIconAsync(instance.Id); Assert.AreEqual((byte)230, AccountAppearancePng.DecodeIcon(image!).Rgba.Span[2]);
        var hash = f.Service.Snapshot.Instances.Single().Presentation!.IconImageSha256!;
        await f.Service.UpdateInstancePresentationAsync(instance.Id, new("auto", "auto", "", false)); CollectionAssert.AreEqual(old!, (await f.Service.ReadInstanceIconAsync(instance.Id))!);
        await f.Service.UpdateInstancePresentationAsync(instance.Id, new("custom", "auto", "", false)); CollectionAssert.AreEqual(image!, (await f.Service.ReadInstanceIconAsync(instance.Id))!);
        Assert.IsTrue(File.Exists(Path.Combine(f.Platform.Paths.LocalDataDirectory, "instance-icons", hash + ".png"))); Assert.IsTrue(File.Exists(f.Config + ".bak"));
        await using var store = new Configuration.ConfigurationStore(f.Platform.Paths); Assert.AreEqual(hash, (await store.LoadAsync()).Instances.Single().Presentation!.IconImageSha256);
        CollectionAssert.AreEqual(before, Files(root)); Assert.IsFalse(Directory.EnumerateFiles(f.Platform.Paths.LocalDataDirectory, "*.tmp-*", SearchOption.AllDirectories).Any()); Assert.AreEqual(0, f.Guard.Calls);
    }
    [TestMethod, DataRow("cancel"), DataRow("save-failure"), DataRow("nonsquare"), DataRow("crc")]
    public async Task FailedOrCancelledCustomIconKeepsPreviousChoiceAndCleansNewFiles(string scenario)
    {
        await using var f = await Fixture.Create(); var instance = await f.Service.CreateInstanceAsync("fixture", "1.21.1"); await f.Service.SaveInstanceIconAsync(instance.Id, Solid(256, 256, 1, 2, 3));
        var prior = f.Service.Snapshot.Instances.Single().Presentation; var files = Files(Path.Combine(f.Platform.Paths.LocalDataDirectory, "instance-icons")); var png = scenario == "nonsquare" ? Solid(256, 128, 4, 5, 6) : Solid(256, 256, 4, 5, 6); if (scenario == "crc") png[^1] ^= 1;
        using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "save-failure") { File.Move(f.Config, f.Config + ".held"); Directory.CreateDirectory(f.Config); }
        try { await Reject(() => f.Service.SaveInstanceIconAsync(instance.Id, png, cts.Token)); Assert.AreEqual(prior, f.Service.Snapshot.Instances.Single().Presentation); CollectionAssert.AreEqual(files, Files(Path.Combine(f.Platform.Paths.LocalDataDirectory, "instance-icons"))); }
        finally { if (scenario == "save-failure") { Directory.Delete(f.Config); File.Move(f.Config + ".held", f.Config); } }
    }
    [TestMethod]
    public async Task CustomArtworkBelongsToDataRootAndSurvivesChangingExecutableDirectory()
    {
        var firstExecutable = Path.Combine(TestFiles.Root(), "read-only-app"); var secondExecutable = Path.Combine(TestFiles.Root(), "replacement-app");
        await using var f = await Fixture.Create(firstExecutable); var instance = await f.Service.CreateInstanceAsync("portable icon", "1.21.1");
        await f.Service.SaveInstanceIconAsync(instance.Id, Solid(256, 256, 17, 31, 47)); Assert.IsFalse(Directory.Exists(firstExecutable));
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network"))); var downloader = new VerifiedDownloader(http);
        var moved = f.Platform with { Paths = new AppPaths(new(f.Platform.Paths.DataDirectory), f.Platform.Info.Context, executableDirectory: secondExecutable) };
        await using var reopened = new LauncherService(moved, http, downloader, new NetworkOptions()); await reopened.InitializeAsync();
        var png = await reopened.ReadInstanceIconAsync(instance.Id); Assert.AreEqual((byte)47, AccountAppearancePng.DecodeIcon(png!).Rgba.Span[2]); Assert.IsFalse(Directory.Exists(secondExecutable));
        Assert.IsTrue(Directory.Exists(Path.Combine(f.Platform.Paths.LocalDataDirectory, "instance-icons"))); Assert.AreEqual(0, f.Guard.Calls);
    }
    [TestMethod]
    public void CropPositionZoomAndTransparentEdgesProduceExactSquarePixels()
    {
        var pixels = new byte[8 * 4 * 4]; for (var y = 0; y < 4; y++) for (var x = 0; x < 8; x++) { var o = (y * 8 + x) * 4; pixels[o] = x < 4 ? (byte)255 : (byte)0; pixels[o + 2] = x < 4 ? (byte)0 : (byte)255; pixels[o + 3] = 255; }
        var source = new InstanceIconPixels(8, 4, pixels); var left = AccountAppearancePng.DecodeIcon(InstanceIconImages.Crop(source, 2, 0, .5)); var right = AccountAppearancePng.DecodeIcon(InstanceIconImages.Crop(source, 2, 1, .5));
        Assert.AreEqual(256, left.Width); Assert.AreEqual(256, left.Height); Assert.AreEqual((byte)255, left.Rgba.Span[0]); Assert.AreEqual((byte)255, right.Rgba.Span[2]);
        Assert.ThrowsExactly<ArgumentException>(() => InstanceIconImages.Crop(source, double.NaN));
        Assert.ThrowsExactly<OperationCanceledException>(() => InstanceIconImages.Crop(source, ct: new CancellationToken(true)));
    }
    [TestMethod, DataRow("liteloader", true), DataRow("fabric", false), DataRow("optifine", false)]
    public async Task LegacyLitemodCanBeManagedOnlyByItsCompatibleLoader(string loader, bool compatible)
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "game"); Directory.CreateDirectory(game); var source = Path.Combine(root, "public.litemod"); await File.WriteAllTextAsync(source, "controlled local file");
        var instance = new GameInstance("test", "test", "1.7.10", game, Loader: loader);
        if (!compatible) { await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InstanceWorkspace.ImportAsync(instance, "mods", source, default)); Assert.IsFalse(Directory.Exists(Path.Combine(game, "mods"))); return; }
        await InstanceWorkspace.ImportAsync(instance, "mods", source, default); var file = InstanceWorkspace.List(instance, "mods").Single(); InstanceWorkspace.ToggleMod(instance, file.RelativePath, false);
        Assert.IsFalse(InstanceWorkspace.List(instance, "mods").Single().Enabled); InstanceWorkspace.ToggleMod(instance, file.RelativePath + ".disabled", true); Assert.AreEqual("controlled local file", await File.ReadAllTextAsync(Path.Combine(game, "mods/public.litemod")));
    }
    internal static byte[] Solid(int w, int h, byte r, byte g, byte b) { var data = new byte[w * h * 4]; for (var i = 0; i < data.Length; i += 4) { data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = 255; } return ToolImages.EncodeRgba(w, h, data); }
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().Select(p => Path.GetRelativePath(root, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    private static async Task Reject(Func<Task> action) { try { await action(); Assert.Fail("Rejected operation unexpectedly succeeded"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or OperationCanceledException or AggregateException) { } }
    private sealed class NoGame : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) { Calls++; throw new AssertFailedException("Game/process execution forbidden"); }
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) { Calls++; throw new AssertFailedException("Game/process execution forbidden"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public PlatformServices Platform = null!; public LauncherService Service = null!; public NoGame Guard = new(); private HttpClient http = null!; private ProcessRunner runner = null!;
        public string Config => Path.Combine(Platform.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        public static async Task<Fixture> Create(string? executableDirectory = null)
        {
            var f = new Fixture(); f.http = new(new ScriptedHttpHandler(_ => throw new AssertFailedException("No download expected"))); var downloader = new VerifiedDownloader(f.http);
            var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); f.runner = (ProcessRunner)platform.Processes; f.Platform = platform with { Processes = f.Guard, Paths = new AppPaths(new(platform.Paths.DataDirectory), platform.Info.Context, executableDirectory: executableDirectory) };
            f.Service = new(f.Platform, f.http, downloader, new NetworkOptions()); await f.Service.InitializeAsync(); await f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings); return f;
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await runner.DisposeAsync(); http.Dispose(); }
    }
}
