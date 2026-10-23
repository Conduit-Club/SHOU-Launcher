using System;
using System.Linq;
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

    [TestMethod]
    public void CustomPeersAreUsedAndCustomOnlyDoesNotAddPublicPeers()
    {
        var custom = "tcp://relay.example:11010";
        CollectionAssert.AreEqual(new[] { custom }, LobbyRelayPolicy.GetPeers($" {custom};{custom}; ", true).ToArray());
        Assert.AreEqual(custom, LobbyRelayPolicy.GetPeers(custom, false)[0]);
        Assert.HasCount(2, LobbyRelayPolicy.GetPeers("", false));
        Assert.Throws<ArgumentException>(() => LobbyRelayPolicy.GetPeers("", true));
        Assert.Throws<ArgumentException>(() => LobbyRelayPolicy.GetPeers("https://remote.example/config", false));
    }
}
