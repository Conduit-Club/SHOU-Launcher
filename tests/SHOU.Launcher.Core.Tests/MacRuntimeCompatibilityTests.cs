using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class MacRuntimeCompatibilityTests
{
    private static JsonObject Vanilla() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/Official-1.12.2/1.12.2.json")))!.AsObject();
    private static RuleContext Mac(RuntimeArchitecture arch=RuntimeArchitecture.Arm64) => new("osx","26.6",arch);
    [TestMethod]
    public void OfficialLegacyMetadataResolvesCompleteArmProfileWithoutMutatingTheSource()
    {
        var metadata=Vanilla();var before=metadata.ToJsonString();var result=LibraryResolver.Resolve(metadata,Mac());
        Assert.AreEqual(before,metadata.ToJsonString());Assert.AreEqual(MacRuntimeCompatibility.Revision,result.CompatibilityProfile);
        Assert.IsTrue(result.Files.Where(f=>f.LegacyNative).All(f=>f.Name.Contains("natives-osx-arm64",StringComparison.Ordinal)));
        Assert.IsTrue(result.Files.Any(f=>f.Name=="net.java.dev.jna:jna:5.13.0"));Assert.IsTrue(result.Files.Any(f=>f.Name=="net.java.dev.jna:jna-platform:5.13.0"));
        Assert.IsTrue(result.Files.Any(f=>f.Name=="com.mojang:text2speech:1.11.3"));Assert.IsTrue(result.Files.Any(f=>f.Name=="ca.weblite:java-objc-bridge:1.1.0-mmachina.1"));
        Assert.IsFalse(result.Files.Any(f=>f.Name.Contains("2.9.2",StringComparison.Ordinal)||f.Name.Contains("jna:4.4.0",StringComparison.Ordinal)));
        Assert.IsTrue(result.Files.All(f=>f.Sha1?.Length==40&&f.Size>0));
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(result.Files),System.Text.Json.JsonSerializer.Serialize(LibraryResolver.Resolve(MacRuntimeCompatibility.Resolve(metadata,Mac()).Metadata,Mac()).Files));
    }
    [TestMethod,DataRow("windows",RuntimeArchitecture.X64),DataRow("windows",RuntimeArchitecture.Arm64),DataRow("linux",RuntimeArchitecture.X64),DataRow("osx",RuntimeArchitecture.X64)]
    public void OtherPlatformsDoNotReceiveTheArmLibraryOverlay(string os,RuntimeArchitecture arch)
    {
        var metadata=Vanilla();var before=metadata.ToJsonString();var profile=MacRuntimeCompatibility.Resolve(metadata,new(os,"fixture",arch));
        Assert.AreSame(metadata,profile.Metadata);Assert.IsNull(profile.Id);Assert.AreEqual(before,metadata.ToJsonString());
    }
    [TestMethod]
    public void WindowsKeepsOriginalJnaNativeUrlsClasspathAndThreadBehavior()
    {
        var metadata=Vanilla();var context=new RuleContext("windows","10.0",RuntimeArchitecture.X64);var libraries=LibraryResolver.Resolve(metadata,context);
        Assert.IsNull(libraries.CompatibilityProfile);Assert.IsTrue(libraries.Files.Any(f=>f.Name=="net.java.dev.jna:jna:4.4.0"));
        Assert.IsFalse(libraries.Files.Any(f=>f.Url.Host=="github.com"||f.Name.Contains("mmachina",StringComparison.Ordinal)||f.RelativePath.Contains("arm64",StringComparison.Ordinal)));
        var plan=LaunchPlanBuilder.Build(Input(metadata,context));Assert.IsFalse(plan.Arguments.Contains("-XstartOnFirstThread"));
        Assert.IsFalse(plan.RedactedDiagnostics.Any(d=>d.Contains("MacRuntimeProfile",StringComparison.Ordinal)));
    }
    [TestMethod]
    public void LoaderMergeIsPatchedAtResolutionAndLwjgl2UsesAwtInsteadOfFirstThread()
    {
        var metadata=VersionMetadataResolver.Merge(Vanilla(),JsonNode.Parse("""{"id":"1.12.2-forge-14.23.5.2860","mainClass":"net.minecraft.launchwrapper.Launch","libraries":[{"name":"net.minecraft:launchwrapper:1.12"}]}""")!.AsObject());
        var plan=LaunchPlanBuilder.Build(Input(metadata,Mac()));Assert.IsFalse(plan.Arguments.Contains("-XstartOnFirstThread"));
        Assert.IsTrue(plan.ClasspathEntries.Any(p=>p.EndsWith("lwjgl-2.9.4-nightly-20150209.jar",StringComparison.Ordinal)));
        Assert.IsTrue(plan.RedactedDiagnostics.Any(d=>d.Contains(MacRuntimeCompatibility.Revision,StringComparison.Ordinal)));
        Assert.IsTrue(plan.ClasspathEntries.Any(p=>p.EndsWith("launchwrapper-1.12.jar",StringComparison.Ordinal)));
    }
    [TestMethod]
    public void ModernMacStillStartsOnFirstThreadAndIntelLegacyCanResolveOptionalNarratorArtifacts()
    {
        var old=Vanilla();Assert.IsFalse(LaunchPlanBuilder.Build(Input(old,Mac(RuntimeArchitecture.X64))).Arguments.Contains("-XstartOnFirstThread"));
        var modern=(JsonObject)old.DeepClone();modern["libraries"]=new JsonArray(new JsonObject { ["name"]="org.lwjgl:lwjgl:3.3.3" });
        Assert.IsTrue(LaunchPlanBuilder.Build(Input(modern,Mac())).Arguments.Contains("-XstartOnFirstThread"));
    }
    [TestMethod]
    public void UniversalMachOAcceptsItsMatchingSlicesAndJniAliasRetainsVerifiedBytes()
    {
        var root=TestFiles.Root();try
        {
            var bytes=Fat();using(var input=new MemoryStream(bytes)) { var a=MacNativeLibraries.Architectures(input);Assert.IsTrue(a.Contains(RuntimeArchitecture.Arm64)&&a.Contains(RuntimeArchitecture.X64)); }
            var file=Path.Combine(root,"libjinput-osx.jnilib");File.WriteAllBytes(file,bytes);
            var paths=MacNativeLibraries.ValidateAndAlias([file],Mac());Assert.AreEqual(2,paths.Count);
            CollectionAssert.AreEqual(bytes,File.ReadAllBytes(Path.ChangeExtension(file,".dylib")));
            var wrong=Path.Combine(root,"libwrong.dylib");File.WriteAllBytes(wrong,Thin(RuntimeArchitecture.X64));
            Assert.ThrowsExactly<PlatformNotSupportedException>(()=>MacNativeLibraries.ValidateAndAlias([wrong],Mac()));
        }finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public void McMouserNativeOverrideIsCheckedOnlyForMacAndUnrelatedModsAreNotRejected()
    {
        var root=TestFiles.Root();try
        {
            var mods=Path.Combine(root,"mods");Directory.CreateDirectory(mods);
            using(var z=ZipFile.Open(Path.Combine(mods,"unrelated-native.jar"),ZipArchiveMode.Create))using(var s=z.CreateEntry("natives/windows.dll").Open())s.Write(Thin(RuntimeArchitecture.X64));
            MacNativeLibraries.ValidateMcMouser(root,Mac());
            using(var z=ZipFile.Open(Path.Combine(mods,"mcmouser-fixture.jar"),ZipArchiveMode.Create))using(var s=z.CreateEntry("mcmouser/natives/liblwjgl.dylib").Open())s.Write(Thin(RuntimeArchitecture.X64));
            MacNativeLibraries.ValidateMcMouser(root,new("windows","10",RuntimeArchitecture.Arm64));
            Assert.ThrowsExactly<PlatformNotSupportedException>(()=>MacNativeLibraries.ValidateMcMouser(root,Mac()));
        }finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public void MalformedMachOIsNotTreatedAsAnArmLibrary()
    {
        using var invalid=new MemoryStream(new byte[8]);Assert.AreEqual(0,MacNativeLibraries.Architectures(invalid).Count);
        using var truncated=new MemoryStream([0xca,0xfe,0xba,0xbe,0,0,0,2]);Assert.ThrowsExactly<EndOfStreamException>(()=>MacNativeLibraries.Architectures(truncated));
    }
    [TestMethod]
    public void LegacyForgeEntryIsRestrictedToVerifiedMacProfileIdentity()
    {
        var profile=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/Official-1.12.2/forge-2860.json")))!.AsObject();
        SHOU.Launcher.Core.Loaders.OfficialLoaderInstaller.ValidateMainClass(profile,"forge","1.12.2",LauncherOS.MacOS);
        foreach(var os in new[]{LauncherOS.Windows,LauncherOS.Linux,LauncherOS.Unknown})
            Assert.ThrowsExactly<InvalidDataException>(()=>SHOU.Launcher.Core.Loaders.OfficialLoaderInstaller.ValidateMainClass(profile,"forge","1.12.2",os));
        var merged=VersionMetadataResolver.Merge(Vanilla(),profile);
        Assert.IsTrue(SHOU.Launcher.Core.Loaders.OfficialLoaderInstaller.IsLegacyMacForge(merged,"1.12.2",LauncherOS.MacOS));
        profile["minecraftArguments"]="--tweakClass untrusted.Tweaker";
        Assert.IsFalse(SHOU.Launcher.Core.Loaders.OfficialLoaderInstaller.IsLegacyMacForge(profile,"1.12.2",LauncherOS.MacOS));
    }
    private static LaunchInputs Input(JsonObject metadata,RuleContext context)
    {
        var root=TestFiles.Root();return new(new("1.12.2",metadata,"fixture.json",["1.12.2"]),new("/fixture/java","/fixture","1.8.0-fixture",8,"test",context.JavaArchitecture,"fixture"),context,new("fixture",new string('1',32),"0"),root,Path.Combine(root,"libraries"),Path.Combine(root,"assets"),Path.Combine(root,"natives"),Path.Combine(root,"client.jar"),"1.12",2048);
    }
    private static byte[] Thin(RuntimeArchitecture arch) { var b=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(b,0xfeedfacf);BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4),arch==RuntimeArchitecture.Arm64?0x0100000cu:0x01000007u);return b; }
    private static byte[] Fat() { var b=new byte[48];BinaryPrimitives.WriteUInt32BigEndian(b,0xcafebabe);BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4),2);BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8),0x0100000c);BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28),0x01000007);return b; }
}
