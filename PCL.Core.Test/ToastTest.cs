using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

using static PCL.Core.UI.ToastNotification;

namespace PCL.Core.Test;

[TestClass]
public class ToastTest
{
    [TestMethod]
    public void TestToast()
    {
        var xml = BuildToastXml("A <toast> & notice", "Test & Toast");

        StringAssert.Contains(xml, "<text>Test &amp; Toast</text>");
        StringAssert.Contains(xml, "<text>A &lt;toast&gt; &amp; notice</text>");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void TestToast_Integration()
    {
        if (!IsExternalTestEnabled())
        {
            Assert.Inconclusive("Toast UI integration test requires PCL_RUN_EXTERNAL_TESTS=1.");
        }

        SendToast("A toast notice from PCL.Core!", "Test Toast");
    }

    private static bool IsExternalTestEnabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable("PCL_RUN_EXTERNAL_TESTS"), "1",
            StringComparison.OrdinalIgnoreCase);
    }
}
