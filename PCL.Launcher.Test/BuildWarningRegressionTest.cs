using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Launcher.Test;

[TestClass]
public sealed class BuildWarningRegressionTest
{
    [TestMethod]
    public void DevelopmentAssemblyCannotBeDistributedAsStandaloneLauncher()
    {
        var method = typeof(PCL.UpdateManager).GetMethod("DownloadLatestPCL",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var error = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, [null]));
        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
    }

    [TestMethod]
    public async Task ImageLoadersRejectMissingTargetBeforeLoading()
    {
        var base64Error = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => ImageLoaderHelper.SetImageFromBase64Async("", null!));
        Assert.AreEqual("imageElement", base64Error.ParamName);
        var fileError = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => ImageLoaderHelper.SetImageFromFileAsync("", null!, ""));
        Assert.AreEqual("imageElement", fileError.ParamName);
    }

    [TestMethod]
    [DataRow("Notch", "b50ad385829d3141a2167e7d7539ba7f")]
    [DataRow("Steve", "5627dd98e6be3c21b8a8e92344183641")]
    public void OfflineUuidKeepsJavaByteOrder(string name, string expected)
    {
        Assert.AreEqual(expected, PCL.ProfileUi.GetOfflineUuid(name));
        Assert.AreEqual(Guid.Parse(expected).ToString(), PCL.ProfileUi.GetOfflineUuid(name, isSplited: true));
    }
}
