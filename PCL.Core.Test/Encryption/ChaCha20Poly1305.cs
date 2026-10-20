using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.Encryption;

[TestClass]
public class ChaCha20Poly1305
{
    [TestMethod]
    public void ChaCha20Poly1305_IsSupportedMatchesPlatform()
    {
        Assert.AreEqual(System.Security.Cryptography.ChaCha20Poly1305.IsSupported,
            Core.Utils.Encryption.ChaCha20Poly1305Provider.Instance.IsSupported);
    }

    [TestMethod]
    [TestCategory("HardwareCrypto")]
    public void TestChaCha20Simple()
    {
        if (!Core.Utils.Encryption.ChaCha20Poly1305Provider.Instance.IsSupported)
        {
            Assert.Inconclusive("ChaCha20Poly1305 is not supported by this platform.");
        }

        var randomData = new byte[1024];
        Random.Shared.NextBytes(randomData);

        var randomKey = new byte[32];
        RandomNumberGenerator.Fill(randomKey);

        var encryptedData = Core.Utils.Encryption.ChaCha20Poly1305Provider.Instance.Encrypt(randomData, randomKey);
        var decryptedData = Core.Utils.Encryption.ChaCha20Poly1305Provider.Instance.Decrypt(encryptedData, randomKey);

        Assert.AreEqual(randomData.Length, decryptedData.Length);
        for (var i = 0; i < decryptedData.Length; i++)
            Assert.AreEqual(decryptedData[i], randomData[i]);
    }
}
