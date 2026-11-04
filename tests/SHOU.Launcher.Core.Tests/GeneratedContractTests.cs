using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class GeneratedContractTests
{
    [TestMethod] public void MetadataResourceIsReal()
    {
        var build = new BuildConfiguration().GetIdentity();
        Assert.AreEqual("1.5.0", build.Version);
        Assert.AreEqual("潮涌核心社启动器", build.Name);
        Assert.AreEqual("SHOU.Launcher.Core", build.OwnerAssembly);
    }
    [TestMethod] public void GeneratedRegistrationsAreConsumed()
    {
        CollectionAssert.Contains(GeneratedSettingsSchema.Entries.ToArray(), "JavaExecutable");
        Assert.IsLessThan(GeneratedLifecycle.Entries.ToList().IndexOf("Configuration"), GeneratedLifecycle.Entries.ToList().IndexOf("DataLock"));
    }
    [TestMethod] public void CoreDoesNotReferenceWindowsDesktop()
    {
        var names = typeof(BuildConfiguration).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.IsFalse(names.Any(n => n is "PCL.Core" or "PresentationFramework" or "WindowsBase" or "SHOU.Launcher.Desktop"));
    }
}
