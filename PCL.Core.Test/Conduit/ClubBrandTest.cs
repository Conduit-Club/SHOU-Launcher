using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class ClubBrandTest
{
    [TestMethod]
    public void NewGameTypeInfoUsesClubBrand()
    {
        Assert.AreEqual("潮涌核心社", ClubCatalog.BrandName);
        Assert.AreEqual("Conduit Club", ClubCatalog.BrandNameEnglish);
        Assert.AreEqual(ClubCatalog.BrandName, ClubCatalog.GameTypeInfo);
        Assert.AreEqual("潮涌核心社启动器", ClubCatalog.LauncherName);
        Assert.AreEqual("Conduit Club Launcher", ClubCatalog.LauncherNameEnglish);
    }

    [TestMethod]
    public void LegacyDefaultIsMigratedWithoutTouchingUserValues()
    {
        Assert.AreEqual(ClubCatalog.GameTypeInfo,
            ClubCatalog.MigrateLegacyGameTypeInfo(ClubCatalog.LegacyGameTypeInfo));
        Assert.AreEqual(string.Empty, ClubCatalog.MigrateLegacyGameTypeInfo(string.Empty));
        Assert.AreEqual("我的自定义版本", ClubCatalog.MigrateLegacyGameTypeInfo("我的自定义版本"));
        Assert.AreEqual("pclce", ClubCatalog.MigrateLegacyGameTypeInfo("pclce"));
        Assert.AreEqual(ClubCatalog.GameTypeInfo,
            ClubCatalog.MigrateLegacyGameTypeInfo(ClubCatalog.GameTypeInfo));
    }
}
