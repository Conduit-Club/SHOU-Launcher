using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LaunchPlanTests
{
    [TestMethod, DataRow("windows"), DataRow("osx")]
    public void BootstrapIgnoreListMatchesInheritedClientWithoutChangingProfileIdentity(string os)
    {
        var input = Inputs(os); input.Version.Metadata["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher";
        input.Version.Metadata["arguments"]!["jvm"]!.AsArray().Add("-DignoreList=client-extra,${version_name}.jar");
        input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("--version"); input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("${version_name}");
        input = input with { Version = input.Version with { Id = "neoforge-21.1.262" }, ClientJar = Path.Combine(input.GameDirectory, "1.21.1.jar") };
        var plan = LaunchPlanBuilder.Build(input); var arguments = plan.Arguments.ToList();
        CollectionAssert.Contains(arguments, "-DignoreList=client-extra,neoforge-21.1.262.jar,1.21.1.jar");
        Assert.AreEqual("neoforge-21.1.262", arguments[arguments.IndexOf("--version") + 1]);
        Assert.AreEqual(input.ClientJar, plan.ClasspathEntries.Last());
    }
    [TestMethod]
    public void MatchingJarNameIsNotDuplicatedAndOtherMainClassesAreUnchanged()
    {
        var input = Inputs(); input.Version.Metadata["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher";
        var jvm = input.Version.Metadata["arguments"]!["jvm"]!.AsArray(); jvm.Add("-DignoreList=client-extra,${version_name}.jar");
        input = input with { ClientJar = Path.Combine(input.GameDirectory, "1.21.1.jar") };
        CollectionAssert.Contains(LaunchPlanBuilder.Build(input).Arguments.ToArray(), "-DignoreList=client-extra,1.21.1.jar");
        input.Version.Metadata["mainClass"] = "net.minecraft.client.main.Main"; input = input with { Version = input.Version with { Id = "custom-profile" } };
        CollectionAssert.Contains(LaunchPlanBuilder.Build(input).Arguments.ToArray(), "-DignoreList=client-extra,custom-profile.jar");
    }
}
