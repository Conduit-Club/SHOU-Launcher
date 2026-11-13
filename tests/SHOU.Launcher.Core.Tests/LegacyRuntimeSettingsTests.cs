using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LegacyDesktopStartupTests
{
    [TestMethod, DataRow(2, 512), DataRow(12, 1536), DataRow(13, 2048), DataRow(15, 3072),
        DataRow(21, 6144), DataRow(25, 8192), DataRow(26, 9216), DataRow(33, 16384), DataRow(34, 18432), DataRow(41, 32768)]
    public void OldMemoryTicksKeepTheirOriginalMiBMeaning(int tick, int expected)
    {
        var draft = LegacyLaunchSettings.Map(new Dictionary<string, string> { ["VersionRamType"] = "1", ["VersionRamCustom"] = tick.ToString(System.Globalization.CultureInfo.InvariantCulture) }, new());
        Assert.AreEqual(expected, draft.MemoryMiB); Assert.IsEmpty(draft.Warnings);
    }

    [TestMethod, DataRow("0"), DataRow("1"), DataRow("42"), DataRow("-1"), DataRow("2147483647"), DataRow("25.5"), DataRow("25,0"), DataRow("not-a-tick")]
    public void InvalidMemoryNeverBecomesAnUnintendedHeapSize(string tick)
    {
        var draft = LegacyLaunchSettings.Map(new Dictionary<string, string> { ["VersionRamType"] = "1", ["VersionRamCustom"] = tick }, new());
        Assert.IsNull(draft.MemoryMiB); Assert.IsNotEmpty(draft.Warnings);
    }

    [TestMethod]
    public void InitialHeapIsBoundedByTheImportedMaximumAndFollowGlobalKeepsNoInstanceOverride()
    {
        var values = new Dictionary<string, string> { ["VersionRamType"] = "1", ["VersionRamCustom"] = "15", ["VersionRamCustomInitial"] = "41" };
        var draft = LegacyLaunchSettings.Map(values, new());
        Assert.AreEqual(3072, draft.MemoryMiB); Assert.AreEqual(3072, draft.Options.MinimumMemoryMiB);
        values["VersionRamType"] = "2"; var inherited = LegacyLaunchSettings.Map(values, new());
        Assert.IsNull(inherited.MemoryMiB); Assert.IsNull(inherited.Options.MinimumMemoryMiB);
        values["VersionRamType"] = "0"; Assert.IsNotEmpty(LegacyLaunchSettings.Map(values, new(AutomaticMemory: false)).Warnings);
    }

    [TestMethod]
    public void JavaFormatsResolveExistingExecutablesWithoutRunningThemAndRejectEscapesOrConflictingFields()
    {
        var root = TestFiles.Root(); var bin = Path.Combine(root, "Java 中文", "bin"); Directory.CreateDirectory(bin);
        var java = Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"); File.WriteAllText(java, "not executed fixture");
        string[] preferences = [java, JsonSerializer.Serialize(new { Path = bin, VersionString = "17.0.1", Is64Bit = true }),
            JsonSerializer.Serialize(new { kind = "exist", JavaExePath = java }),
            JsonSerializer.Serialize(new { kind = "relative", RelativePath = Path.GetRelativePath(root, java) }),
            "$" + Path.DirectorySeparatorChar + Path.GetRelativePath(root, java)];
        foreach (var preference in preferences)
        {
            var draft = LegacyLaunchSettings.Map(new Dictionary<string, string> { ["VersionArgumentJavaSelect"] = preference }, new(), root);
            Assert.AreEqual(java, draft.JavaExecutable); Assert.IsEmpty(draft.Warnings);
        }
        foreach (var preference in new[] { "使用全局设置", "", "{\"kind\":\"auto\"}", "{\"kind\":\"global\"}" })
            Assert.IsNull(LegacyLaunchSettings.Map(new Dictionary<string, string> { ["VersionArgumentJavaSelect"] = preference }, new(), root).JavaExecutable);
        foreach (var preference in new[] { "{\"kind\":\"relative\",\"RelativePath\":\"../java\"}", "{\"kind\":\"exist\",\"JavaExePath\":\"FIRST\",\"javaexepath\":\"SECOND\"}", "synthetic-unreadable-java" })
        {
            var draft = LegacyLaunchSettings.Map(new Dictionary<string, string> { ["VersionArgumentJavaSelect"] = preference }, new(), root);
            Assert.IsNull(draft.JavaExecutable); Assert.IsNotEmpty(draft.Warnings); Assert.IsFalse(string.Join(" ", draft.Warnings).Contains(preference));
        }
        Assert.AreEqual("not executed fixture", File.ReadAllText(java));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task StartupInheritsRealPreferencesOnceAndPreservesSourceAndSubsequentNewSettings()
    {
        await using var f = new Fixture(); var folder = LegacyInstanceManagementTests.Profile(f.Source, "Runtime fixture");
        var java = Path.Combine(f.Source, OperatingSystem.IsWindows() ? "java.exe" : "java"); await File.WriteAllTextAsync(java, "no execution");
        var pcl = Path.Combine(folder, "PCL"); Directory.CreateDirectory(pcl); var ini = Path.Combine(pcl, "Setup.ini");
        await File.WriteAllTextAsync(ini, "VersionArgumentJavaSelect:" + JsonSerializer.Serialize(new { Path = java }) + "\nVersionRamType:1\nVersionRamCustom:25\nVersionRamCustomInitial:15\nFutureKeep:yes\n");
        var bytes = await File.ReadAllBytesAsync(ini);
        f.Data = new([f.Source], [], selectedVersionDirectory: folder, launchSettings: new Dictionary<string, string> {
            ["LaunchArgumentJavaSelect"] = java, ["LaunchRamType"] = "1", ["LaunchRamCustom"] = "21", ["LaunchRamCustomInitial"] = "13" }, executableDirectory: f.Source);
        await f.StartAsync(); var imported = f.Service.Snapshot.Instances.Single();
        Assert.AreEqual(java, imported.JavaExecutable); Assert.AreEqual(8192, imported.MemoryMiB); Assert.AreEqual(3072, imported.LaunchOptions!.MinimumMemoryMiB);
        Assert.AreEqual(java, f.Service.Snapshot.Settings.JavaExecutable); Assert.AreEqual(6144, f.Service.Snapshot.Settings.MemoryMiB);
        Assert.AreEqual(2048, f.Service.Snapshot.Settings.MinimumMemoryMiB); Assert.IsFalse(f.Service.Snapshot.Settings.LaunchOptions!.AutomaticMemory);
        Assert.AreEqual(imported.Id, f.Service.Snapshot.SelectedInstanceId);
        await f.Service.UpdateInstanceAsync(imported.Id, new(imported.Name, null, 4096, new(MinimumMemoryMiB: 512)));
        await f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings with { JavaExecutable = null, MemoryMiB = 5120 });
        await f.StartAsync(); var reloaded = f.Service.Snapshot.Instances.Single();
        Assert.AreEqual(4096, reloaded.MemoryMiB); Assert.IsNull(reloaded.JavaExecutable);
        Assert.AreEqual(5120, f.Service.Snapshot.Settings.MemoryMiB); Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable);
        Assert.AreEqual(1, f.Reads); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(ini)); Assert.AreEqual("no execution", await File.ReadAllTextAsync(java));
    }

    [TestMethod]
    public async Task ReaderAllowsOnlyRuntimeKeysAndHonorsLocalYamlOverSharedAndIniWithoutWritingAnySource()
    {
        var root = TestFiles.Root(); var pcl = Path.Combine(root, "PCL"); var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(pcl); Directory.CreateDirectory(shared);
        var files = new Dictionary<string, string> {
            [Path.Combine(shared,"config.v1.json")] = "{\"LaunchRamType\":0,\"LaunchRamCustom\":15,\"LaunchArgumentJavaSelect\":\"\",\"UnrelatedSecret\":\"synthetic\"}",
            [Path.Combine(pcl,"config.v1.yml")] = "LaunchRamType: 1\nLaunchRamCustom: 25\nLaunchRamCustomInitial: 13\nUnrelatedSecret: synthetic\n",
            [Path.Combine(pcl,"Setup.ini")] = "LaunchRamCustom:21\nUnrelatedSecret:synthetic\n" };
        foreach (var pair in files) await File.WriteAllTextAsync(pair.Key, pair.Value);
        var before = files.Keys.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var data = await new WindowsLegacyDesktopDataReader(root, shared).ReadAsync();
        Assert.AreEqual("1", data.LaunchSettings["LaunchRamType"]); Assert.AreEqual("25", data.LaunchSettings["LaunchRamCustom"]);
        Assert.AreEqual("13", data.LaunchSettings["LaunchRamCustomInitial"]); Assert.IsFalse(data.LaunchSettings.ContainsKey("UnrelatedSecret"));
        Assert.AreEqual(Path.GetFullPath(root), data.ExecutableDirectory);
        foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key)));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task ExplicitFolderAttachmentAlsoInheritsRuntimePreferencesAndInvalidValuesKeepTheProfileUsable()
    {
        await using var f = new Fixture(); var folder = LegacyInstanceManagementTests.Profile(f.Source, "Attach runtime");
        Directory.CreateDirectory(Path.Combine(folder,"PCL")); var ini=Path.Combine(folder,"PCL/Setup.ini");
        await File.WriteAllTextAsync(ini,"VersionRamType:1\nVersionRamCustom:21\n"); var bytes=await File.ReadAllBytesAsync(ini);
        await f.StartAsync(); var attached=await f.Service.AttachExistingInstanceAsync((await f.Service.InspectInstanceFolderAsync(folder)).Single());
        Assert.AreEqual(6144,attached.MemoryMiB); CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(ini));
        var other=LegacyInstanceManagementTests.Profile(f.Source,"Invalid runtime");Directory.CreateDirectory(Path.Combine(other,"PCL"));
        await File.WriteAllTextAsync(Path.Combine(other,"PCL/Setup.ini"),"VersionRamType:1\nVersionRamCustom:not-a-tick\n");
        var retained=await f.Service.AttachExistingInstanceAsync((await f.Service.InspectInstanceFolderAsync(other)).Single());
        Assert.IsNull(retained.MemoryMiB);Assert.IsTrue(retained.Installed);Assert.HasCount(2,f.Service.Snapshot.Instances);
    }
}
