using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class InstanceAdvancedTests
{
    [TestMethod]
    public async Task DirectoryOverrideIsExplicitReversibleAndFollowsSavedGlobalPolicyWithoutMovingFiles()
    {
        await using var f = await Fixture.Create(); var i = await f.Service.CreateInstanceAsync("目录 中文", "1.21.1");
        var marker = Path.Combine(i.GameDirectory, "keep.txt"); await File.WriteAllTextAsync(marker, "original"); var edit = new InstanceEdit(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.Shared);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.UpdateInstanceAsync(i.Id, edit)); Assert.AreEqual(i.GameDirectory, f.Service.Snapshot.Instances.Single().GameDirectory);
        await f.Service.UpdateInstanceAsync(i.Id, edit with { ConfirmDirectoryChange = true }); var shared = f.Service.Snapshot.Instances.Single(); Assert.AreNotEqual(i.GameDirectory, shared.GameDirectory); Assert.IsFalse(Directory.Exists(shared.GameDirectory)); Assert.AreEqual("original", await File.ReadAllTextAsync(marker));
        await f.Service.UpdateInstanceAsync(i.Id, edit with { DirectoryMode = InstanceDirectoryMode.Isolated, ConfirmDirectoryChange = true }); Assert.AreEqual(i.GameDirectory, f.Service.Snapshot.Instances.Single().GameDirectory);
        await f.Service.UpdateInstanceAsync(i.Id, edit with { DirectoryMode = InstanceDirectoryMode.FollowGlobal, ConfirmDirectoryChange = true });
        await f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings with { LaunchOptions = new(Isolation: InstanceIsolation.Shared) }); Assert.AreEqual(shared.GameDirectory, f.Service.Snapshot.Instances.Single().GameDirectory);
        await using var store = new ConfigurationStore(f.Platform.Paths); var loaded = await store.LoadAsync(); Assert.AreEqual(shared.GameDirectory, loaded.Instances.Single().GameDirectory); Assert.AreEqual(InstanceDirectoryMode.FollowGlobal, loaded.Instances.Single().Directories!.Mode); Assert.IsTrue(File.Exists(f.Config + ".bak")); Assert.AreEqual(0, f.Guard.Calls);
        Assert.AreEqual(shared.GameDirectory, await f.Service.GetInstanceDirectoryAsync(i.Id, createMissing: true)); Assert.IsTrue(Directory.Exists(shared.GameDirectory)); Assert.AreEqual("original", await File.ReadAllTextAsync(marker));
    }
    [TestMethod, DataRow("cancel"), DataRow("file"), DataRow("locked")]
    public async Task DirectorySaveFailureOrCancellationKeepsConfigurationAndOriginalFiles(string scenario)
    {
        await using var f = await Fixture.Create(); var i = await f.Service.CreateInstanceAsync("fixture", "1.21.1"); var before = await File.ReadAllBytesAsync(f.Config); var target = InstanceDirectoryPolicy.Describe(i, f.Platform.Paths).SharedDirectory;
        using var cancel = new CancellationTokenSource(); if (scenario == "cancel") cancel.Cancel(); if (scenario == "file") await File.WriteAllTextAsync(target, "external");
        using var held = scenario == "locked" ? new FileStream(f.Config, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        if (scenario == "locked" && !OperatingSystem.IsWindows())
        {
            // Unix permits atomic replacement while the old inode remains open.
            await f.Service.UpdateInstanceAsync(i.Id, new(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.Shared, ConfirmDirectoryChange: true));
            Assert.AreEqual(target, f.Service.Snapshot.Instances.Single().GameDirectory);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Config + ".bak"));
            using var old = new MemoryStream(); await held!.CopyToAsync(old); CollectionAssert.AreEqual(before, old.ToArray()); return;
        }
        await Reject(() => f.Service.UpdateInstanceAsync(i.Id, new(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.Shared, ConfirmDirectoryChange: true), cancel.Token));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Config)); Assert.AreEqual(i.GameDirectory, f.Service.Snapshot.Instances.Single().GameDirectory); if (scenario == "file") Assert.AreEqual("external", await File.ReadAllTextAsync(target)); Assert.IsFalse(Directory.Exists(target));
    }
    [TestMethod]
    public async Task GlobalFollowChangeRollsBackAllInstancesWhenOneTargetIsBlocked()
    {
        await using var f = await Fixture.Create(); var a = await f.Service.CreateInstanceAsync("a", "1.21.1"); var b = await f.Service.CreateInstanceAsync("b", "1.21.1");
        foreach (var i in new[] { a,b }) await f.Service.UpdateInstanceAsync(i.Id, new(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.FollowGlobal));
        var before = await File.ReadAllBytesAsync(f.Config); var target = InstanceDirectoryPolicy.Describe(a, f.Platform.Paths).SharedDirectory; await File.WriteAllTextAsync(target, "external");
        await Assert.ThrowsAsync<IOException>(() => f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings with { LaunchOptions = new(Isolation: InstanceIsolation.Shared) })); CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Config)); Assert.AreEqual(a.GameDirectory, f.Service.Snapshot.Instances.Single(i => i.Id == a.Id).GameDirectory); Assert.AreEqual(b.GameDirectory, f.Service.Snapshot.Instances.Single(i => i.Id == b.Id).GameDirectory);
    }
    [TestMethod]
    public async Task CurrentOrTargetRunningSessionRejectsDirectoryChangesWithoutStartingAProcess()
    {
        await using var f = await Fixture.Create(); var i = await f.Service.CreateInstanceAsync("fixture", "1.21.1"); var registry = new GameSessionRegistry(Path.Combine(f.Platform.Paths.LocalDataDirectory, "game-sessions")); registry.Register(i, Environment.ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.UpdateInstanceAsync(i.Id, new(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.Shared, ConfirmDirectoryChange: true))); Assert.AreEqual(0, f.Guard.Calls);
    }
    [TestMethod, DataRow("True", InstanceDirectoryMode.Isolated), DataRow("False", InstanceDirectoryMode.Shared)]
    public async Task LegacyExplicitIsolationOverridesTheLoaderHeuristicAndDraftReadDoesNotWrite(string value, InstanceDirectoryMode expected)
    {
        await using var f = await Fixture.Create(); var root = TestFiles.Root(); var folder = LegacyInstanceManagementTests.Profile(root, "old", "net.fabricmc:fabric-loader:0.19.3"); Directory.CreateDirectory(Path.Combine(folder,"PCL")); var ini = Path.Combine(folder,"PCL/Setup.ini");
        var text = "VersionArgumentIndieV2:" + value + "\nVersionAdvanceJvm:-XX:+UseG1GC -XX:-OmitStackTraceInFastThrow\nVersionArgumentTitle:{name} old\nVersionAdvanceRun:DO_NOT_EXECUTE\nVersionAdvanceAssetsV2:True\nUnrelatedPrivate:NOT_A_REAL_TOKEN\n"; await File.WriteAllTextAsync(ini,text);
        var candidate = (await ExistingInstanceReader.InspectAsync(folder)).Single(); Assert.AreEqual(expected == InstanceDirectoryMode.Isolated ? folder : root, candidate.GameDirectory);
        var i = await f.Service.AttachExistingInstanceAsync(candidate); var before = await File.ReadAllBytesAsync(f.Config); var draft = await f.Service.ReadLegacyInstanceSettingsAsync(i.Id); Assert.AreEqual(expected,draft.DirectoryMode); Assert.AreEqual("{name} old",draft.Options.WindowTitle); Assert.IsTrue(draft.Warnings.Count >=2); Assert.IsFalse(string.Join(" ",draft.Warnings).Contains("DO_NOT_EXECUTE")); Assert.AreEqual("",draft.Options.BeforeLaunchExecutable); CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(f.Config)); Assert.AreEqual(text,await File.ReadAllTextAsync(ini)); Assert.AreEqual(0,f.Guard.Calls);
    }
    [TestMethod]
    public void LegacyAdvancedArgumentsArePreservedWithoutAnAllowlistAndWarningsDoNotEchoValues()
    {
        var mapped = LegacyLaunchSettings.Map(new Dictionary<string,string>{{"VersionAdvanceJvm","-Djavax.net.ssl.trustStorePassword=NOT_A_REAL_SECRET"},{"VersionAdvanceGame","--accessToken NOT_A_REAL_SECRET"},{"VersionArgumentIndie","2"}},new());
        Assert.AreEqual("-Djavax.net.ssl.trustStorePassword=NOT_A_REAL_SECRET",mapped.Options.JvmArguments); Assert.AreEqual("--accessToken NOT_A_REAL_SECRET",mapped.Options.GameArguments); Assert.AreEqual(InstanceDirectoryMode.Shared,mapped.DirectoryMode); Assert.IsFalse(string.Join(" ",mapped.Warnings).Contains("NOT_A_REAL_SECRET"));
        Assert.HasCount(2,GameSettingsPolicy.JvmArguments(new(JvmArguments:"-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy")));
    }
    [TestMethod]
    public async Task AdvancedValuesAndUnknownFieldsSurviveSaveAndRestartWithIndependentMinimumMemory()
    {
        await using var f = await Fixture.Create(); var i=await f.Service.CreateInstanceAsync("fixture","1.21.1"); var node=JsonNode.Parse(await File.ReadAllTextAsync(f.Config))!;node["instances"]![0]!["futureInstance"]="keep"; await File.WriteAllTextAsync(f.Config,node.ToJsonString());
        // Reload the same sidecar to exercise merging unknown fields, rather than relying on in-memory values.
        await using var loaded=new LauncherService(f.Platform,f.Http,f.Downloader,new());await loaded.InitializeAsync();
        var options=new GameLaunchOptions(MinimumMemoryMiB:512,BeforeLaunchExecutable:Path.Combine(TestFiles.Root(),"controlled.exe"),BeforeLaunchArguments:"--public fixture",ClasspathPrepend:"",UseConsoleJava:true,DebugLogging:true);
        await loaded.UpdateInstanceAsync(i.Id,new("changed",null,1024,options));
        await using var store=new ConfigurationStore(f.Platform.Paths);var saved=await store.LoadAsync();Assert.AreEqual(options,saved.Instances.Single().LaunchOptions);Assert.AreEqual("keep",(string?)JsonNode.Parse(await File.ReadAllTextAsync(f.Config))!["instances"]![0]!["futureInstance"]); Assert.AreEqual(0,f.Guard.Calls);
    }
    [TestMethod]
    public async Task ExplicitClasspathRequiresLocalJarAndKeepsSourceBytes()
    {
        var folder=TestFiles.Root();var path=Path.Combine(folder,"库 中文.jar");using(var z=ZipFile.Open(path,ZipArchiveMode.Create)){using var writer=new StreamWriter(z.CreateEntry("fixture.txt").Open());writer.Write("public");}
        var before=await File.ReadAllBytesAsync(path);Assert.AreEqual(path,GameSettingsPolicy.Classpath(path).Single());CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(path));Assert.Throws<ArgumentException>(()=>GameSettingsPolicy.Classpath("../outside.jar"));Assert.Throws<IOException>(()=>GameSettingsPolicy.Classpath(Path.Combine(folder,"missing.jar")));
    }
    [TestMethod, DataRow(LauncherOS.Windows), DataRow(LauncherOS.MacOS)]
    public void ScriptsContainOnlySafelyQuotedLauncherRouting(LauncherOS os)
    {
        var path=Path.Combine(TestFiles.Root(),"中文 space %NAME% ! & ' ");var script=LaunchScript.Build(os,Path.Combine(path,"SHOU.Launcher.Desktop.exe"),path,"safe-id");Assert.IsTrue(script.Contains("--launch-instance"));Assert.IsFalse(script.Contains("accessToken"));Assert.IsFalse(script.Contains("javaagent"));
        if(os==LauncherOS.Windows){Assert.IsTrue(script.Contains("%%NAME%%"));Assert.IsTrue(script.Contains("DisableDelayedExpansion"));}else Assert.IsTrue(script.Contains("'\"'\"'"));
        Assert.Throws<ArgumentException>(()=>LaunchScript.Build(os,path+"\n",path,"safe-id"));
    }
    [TestMethod, DataRow("cancel"), DataRow("locked"), DataRow("backup-locked")]
    public async Task ScriptFailureRetainsOriginalAndCleansStages(string scenario)
    {
        var root=TestFiles.Root();var path=Path.Combine(root,"launch.cmd");await File.WriteAllTextAsync(path,"original");await File.WriteAllTextAsync(path+".bak","prior-backup");using var cts=new CancellationTokenSource();if(scenario=="cancel")cts.Cancel();using var held=scenario=="cancel"?null:new FileStream(scenario=="locked"?path:path+".bak",FileMode.Open,FileAccess.Read,FileShare.Read);
        if (scenario != "cancel" && !OperatingSystem.IsWindows())
        {
            await LaunchScript.SaveAsync(path,"new",true,false,cts.Token);
            Assert.AreEqual("new",await File.ReadAllTextAsync(path)); Assert.AreEqual("original",await File.ReadAllTextAsync(path+".bak"));
            using var reader = new StreamReader(held!); Assert.AreEqual(scenario == "locked" ? "original" : "prior-backup", await reader.ReadToEndAsync());
            Assert.IsFalse(Directory.GetFiles(root,"*.tmp-*").Any()); return;
        }
        await Reject(()=>LaunchScript.SaveAsync(path,"new",true,false,cts.Token));Assert.AreEqual("original",await File.ReadAllTextAsync(path));Assert.IsFalse(Directory.GetFiles(root,"*.tmp-*").Any());
    }
    [TestMethod]
    public async Task BlockedBackupPublicationPreservesConfigurationAndScriptOnEveryPlatform()
    {
        await using var f = await Fixture.Create(); var i = await f.Service.CreateInstanceAsync("fixture", "1.21.1");
        var before = await File.ReadAllBytesAsync(f.Config); File.Delete(f.Config + ".bak"); Directory.CreateDirectory(f.Config + ".bak");
        await Reject(() => f.Service.UpdateInstanceAsync(i.Id, new(i.Name, null, null, DirectoryMode: InstanceDirectoryMode.Shared, ConfirmDirectoryChange: true)));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Config)); Assert.AreEqual(i.GameDirectory, f.Service.Snapshot.Instances.Single().GameDirectory);
        var path = Path.Combine(TestFiles.Root(), "launch.command"); await File.WriteAllTextAsync(path, "original"); Directory.CreateDirectory(path + ".bak");
        await Reject(() => LaunchScript.SaveAsync(path, "new", true, false, default));
        Assert.AreEqual("original", await File.ReadAllTextAsync(path)); Assert.HasCount(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }
    [TestMethod]
    public void ProductionMacScriptKeepsDefaultCredentialScopeInsteadOfTurningItIntoAnIsolatedRoot()
    {
        var path=TestFiles.Root();var script=LaunchScript.Build(LauncherOS.MacOS,Path.Combine(path,"SHOU.Launcher.Desktop"),path,"fixture",false);Assert.IsFalse(script.Contains("--data-dir"));Assert.IsTrue(script.Contains("--launch-instance 'fixture'"));
    }
    [TestMethod]
    public async Task ScriptOverwriteHasExactBackupAndNoOverwriteModePreservesOldFile()
    {
        var root=TestFiles.Root();var path=Path.Combine(root,"launch.command");await File.WriteAllTextAsync(path,"old");await Assert.ThrowsAsync<IOException>(()=>LaunchScript.SaveAsync(path,"new",false,false,default));await LaunchScript.SaveAsync(path,"new",true,false,default);Assert.AreEqual("new",await File.ReadAllTextAsync(path));Assert.AreEqual("old",await File.ReadAllTextAsync(path+".bak"));
    }
    private static async Task Reject(Func<Task> action){try{await action();Assert.Fail("Unexpected success");}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or OperationCanceledException){}}
    private sealed class NoGame:IProcessRunner
    {
        public int Calls;public Task<ProcessResult> RunAsync(ProcessSpec spec,IProgress<ProcessOutput>? output=null,CancellationToken cancellationToken=default){Calls++;throw new AssertFailedException("No processes allowed");}public Task<IManagedProcess> StartAsync(ProcessSpec spec,IProgress<ProcessOutput>? output=null,CancellationToken cancellationToken=default){Calls++;throw new AssertFailedException("No game allowed");}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public PlatformServices Platform=null!;public LauncherService Service=null!;public HttpClient Http=null!;public VerifiedDownloader Downloader=null!;public NoGame Guard=new();private ProcessRunner runner=null!;public string Config=>Path.Combine(Platform.Paths.LocalDataDirectory,"shou.desktop.v1.json");
        public static async Task<Fixture>Create(){var f=new Fixture();f.Http=new(new ScriptedHttpHandler(_=>throw new AssertFailedException("No network expected")));f.Downloader=new(f.Http);var p=TestPlatformServices.Create(new(TestFiles.Root()),f.Downloader);f.runner=(ProcessRunner)p.Processes;f.Platform=p with{Processes=f.Guard};f.Service=new(f.Platform,f.Http,f.Downloader,new());await f.Service.InitializeAsync();return f;}
        public async ValueTask DisposeAsync(){await Service.DisposeAsync();await runner.DisposeAsync();Http.Dispose();}
    }
}
