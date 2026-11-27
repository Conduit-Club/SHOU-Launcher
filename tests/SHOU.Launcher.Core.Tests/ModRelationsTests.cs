using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ModRelationsTests
{
    private static byte[] Jar(params (string Path, byte[] Bytes)[] entries)
    { using var bytes = new MemoryStream(); using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var entry in entries) { using var output = zip.CreateEntry(entry.Path).Open(); output.Write(entry.Bytes); } return bytes.ToArray(); }
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static byte[] Fabric(string id, string? dependency = null, string? alias = null, (string Path, byte[] Bytes)? child = null)
    {
        var metadata = new Dictionary<string, object> { ["id"] = id, ["name"] = id, ["version"] = "2.0" };
        if (dependency is not null) metadata["depends"] = new Dictionary<string, string> { [dependency] = ">=2.0", ["minecraft"] = "1.21.1" };
        if (alias is not null) metadata["provides"] = new[] { alias };
        if (child is { } nested) { metadata["jars"] = new[] { new { file = nested.Path } }; return Jar(("fabric.mod.json", Json(metadata)), nested); }
        return Jar(("fabric.mod.json", Json(metadata)));
    }
    private static GameInstance Instance(params (string Name, byte[] Bytes)[] files)
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "mods"));
        foreach (var f in files) File.WriteAllBytes(Path.Combine(root, "mods", f.Name), f.Bytes);
        return new("fixture", "Fixture", "1.21.1", root, Loader: "fabric");
    }
    [TestMethod]
    public void RecursiveDeclarationsAndAliasesResolveWithoutExtractingOrChangingFiles()
    {
        var inner = Fabric("inner", alias: "provided_id"); var middle = Fabric("middle", child: ("deep/inner.jar", inner));
        var i = Instance(("outer.jar", Fabric("outer", child: ("META-INF/middle.jar", middle))), ("consumer.jar", Fabric("consumer", "provided_id")));
        var before = Directory.GetFiles(i.GameDirectory, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var r = ModRelations.Read(i); var edge = r.Dependencies.Single();
        Assert.AreEqual(ModDeclarationPresence.Embedded, edge.Presence); Assert.AreEqual(">=2.0", edge.Dependency.Requirement);
        StringAssert.Contains(edge.Providers.Single(), "outer.jar!/META-INF/middle.jar!/deep/inner.jar");
        Assert.AreEqual(3, ModRelations.Flatten(r.Hosts.Single(h => h.Declaration.Ids.Contains("outer")).Declaration).Count()); Assert.IsEmpty(r.Notices);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), Directory.GetFiles(i.GameDirectory, "*", SearchOption.AllDirectories));
        foreach (var file in before) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key));
    }
    [TestMethod]
    public void EmbeddedProviderInDisabledPhysicalHostRemainsDisabled()
    {
        var i = Instance(("host.jar.disabled", Fabric("host", child: ("inner.jar", Fabric("inner")))), ("consumer.jar", Fabric("consumer", "inner")));
        Assert.AreEqual(ModDeclarationPresence.Disabled, ModRelations.Read(i).Dependencies.Single().Presence);
    }
    [TestMethod]
    public void IndependentProviderIsPreferredOverOtherEmbeddedDeclarations()
    {
        var i = Instance(("host.jar", Fabric("host", child: ("inner.jar", Fabric("inner")))), ("inner.jar", Fabric("inner")), ("consumer.jar", Fabric("consumer", "inner")));
        var edge = ModRelations.Read(i).Dependencies.Single(); Assert.AreEqual(ModDeclarationPresence.Independent, edge.Presence); Assert.HasCount(2, edge.Providers);
    }
    [TestMethod]
    public void DamagedChildPreservesParentAndMarksUnresolvedDeclarationsUnverified()
    {
        var i = Instance(("host.jar", Fabric("host", child: ("broken.jar", "not a zip"u8.ToArray()))), ("consumer.jar", Fabric("consumer", "unknown")));
        var r = ModRelations.Read(i); Assert.AreEqual("host", r.Hosts.Single(h => h.RelativePath.EndsWith("host.jar")).Declaration.Ids.Single());
        Assert.AreEqual(ModDeclarationPresence.Unverified, r.Dependencies.Single().Presence); Assert.HasCount(1, r.Notices);
    }
    [TestMethod]
    public void DisabledUnreadableFileDoesNotTaintEnabledConsumerAnalysis()
    {
        var i = Instance(("broken.jar.old", "bad"u8.ToArray()), ("consumer.jar", Fabric("consumer", "missing")));
        Assert.AreEqual(ModDeclarationPresence.NotFound, ModRelations.Read(i).Dependencies.Single().Presence);
    }
    [TestMethod]
    public void DepthBudgetKeepsExistingTreeAndDoesNotAffectImportOrEraseParentIds()
    {
        var bytes = Fabric("leaf"); for (var n = 0; n < 8; n++) bytes = Fabric("node" + n, child: ("nested.jar", bytes));
        var r = ModRelations.Read(Instance(("deep.jar", bytes), ("consumer.jar", Fabric("consumer", "leaf"))));
        Assert.AreEqual(6, ModRelations.Flatten(r.Hosts.Single(h => h.RelativePath.EndsWith("deep.jar")).Declaration).Count());
        Assert.IsNotEmpty(r.Notices); Assert.AreEqual(ModDeclarationPresence.Unverified, r.Dependencies.Single().Presence);
    }
    [TestMethod]
    public void UnsafeNestedPathIsReportedWithoutOpeningOutsideArchive()
    {
        var i = Instance(("host.jar", Fabric("host", child: ("../escape.jar", Fabric("escape"))))); var r = ModRelations.Read(i);
        Assert.IsNotEmpty(r.Notices); Assert.IsEmpty(r.Hosts.Single().Declaration.Children.Single().Ids);
        Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, "escape.jar")));
    }
    [TestMethod]
    public void QuiltDependenciesAliasesAndNestedStringsAreRead()
    {
        var bytes = Jar(("quilt.mod.json", Json(new { quilt_loader = new { id = "quilt_host", version = "1", metadata = new { name = "Quilt Host" }, provides = new[] { new { id = "alias" } }, depends = new[] { new { id = "inner", versions = ">=2", optional = true } }, jars = new[] { "inner.jar" } } })), ("inner.jar", Fabric("inner")));
        var r = ModRelations.Read(Instance(("quilt.jar", bytes)) with { Loader = "quilt" });
        Assert.AreEqual("Quilt Host", r.Hosts.Single().Declaration.Name); Assert.Contains("alias", r.Hosts.Single().Declaration.Ids);
        Assert.IsTrue(r.Dependencies.Single().Dependency.Optional); Assert.AreEqual(ModDeclarationPresence.Embedded, r.Dependencies.Single().Presence);
    }
    [TestMethod]
    public void ForgeSiblingIdsAndJarJarAreIncludedWithRawRequirements()
    {
        var text = "[[mods]]\nmodId=\"first\"\ndisplayName=\"First\"\nversion=\"1\"\n[[mods]]\nmodId=\"second\"\nversion=\"2\"\n[[dependencies.first]]\nmodId=\"inner\"\nmandatory=true\nversionRange=\"[2,3)\"";
        var bytes = Jar(("META-INF/mods.toml", Encoding.UTF8.GetBytes(text)), ("META-INF/jarjar/metadata.json", Json(new { jars = new[] { new { path = "META-INF/jarjar/inner.jar" } } })), ("META-INF/jarjar/inner.jar", Fabric("inner")));
        var r = ModRelations.Read(Instance(("forge.jar", bytes)) with { Loader = "forge" });
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, r.Hosts.Single().Declaration.Ids.ToArray()); Assert.AreEqual("First", r.Hosts.Single().Declaration.Name);
        Assert.AreEqual("[2,3)", r.Dependencies.Single().Dependency.Requirement); Assert.IsFalse(r.Dependencies.Single().Dependency.Optional);
    }
    [TestMethod]
    public void PreCancelledReadDoesNotTouchEvenAnEmptyDirectory()
    { using var c = new CancellationTokenSource(); c.Cancel(); Assert.ThrowsExactly<OperationCanceledException>(() => ModRelations.Read(Instance(), c.Token)); }
    [TestMethod]
    public void MultiLoaderJarUsesTheSelectedLoadersDeclarationAndDoesNotTreatConflictsAsDependencies()
    {
        var text = "[[mods]]\nmodId=\"forge_id\"\n[[dependencies.forge_id]]\nmodId=\"conflict\"\ntype=\"incompatible\"";
        var bytes = Jar(("fabric.mod.json", Json(new { id = "fabric_id", version = "1" })), ("META-INF/neoforge.mods.toml", Encoding.UTF8.GetBytes(text)));
        var r = ModRelations.Read(Instance(("multi.jar", bytes)) with { Loader = "neoforge" });
        Assert.AreEqual("forge_id", r.Hosts.Single().Declaration.Ids.Single()); Assert.IsEmpty(r.Dependencies);
    }
}
