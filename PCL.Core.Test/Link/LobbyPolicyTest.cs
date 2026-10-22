using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Lobby;

namespace PCL.Core.Test.Link;

[TestClass]
public sealed class LobbyPolicyTest
{
    [TestMethod]
    public void LobbyPolicyDoesNotRequireRemoteAnnouncement()
    {
        Assert.IsTrue(LobbyInfoProvider.IsLobbyAvailable);
        Assert.IsTrue(LobbyInfoProvider.AllowCustomName);
        Assert.IsFalse(LobbyInfoProvider.RequiresLogin);
        Assert.IsFalse(LobbyInfoProvider.RequiresRealName);
    }
}
