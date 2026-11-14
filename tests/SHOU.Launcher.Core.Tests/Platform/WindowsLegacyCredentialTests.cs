using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlainToolkit.CngProtectedData;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class WindowsLegacyCredentialTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Entropy = "PCL CE Encryption Key"u8.ToArray();

    [TestMethod]
    [DataRow(1u)]
    [DataRow(2u)]
    public async Task OriginalKeyWrapperVersionsAreReadWithoutMigration(uint version)
    {
        var fixture = new PlatformFixture();
        var path = fixture.Child("UserKey.bin");
        var bytes = Pack(version, [4, 5, 6, 7]); File.WriteAllBytes(path, bytes);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var calls = 0;
        var reader = new WindowsLegacyCredentialReader(path, (wrapper, payload) =>
        {
            calls++; Assert.AreEqual(version, wrapper); CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, payload);
            return Key.ToArray();
        });
        CollectionAssert.AreEqual(Key, (await reader.ReadUserKeyAsync())!);
        Assert.AreEqual("fake account 中文", await reader.DecryptFieldAsync(AuthenticatedField(2, "fake account 中文")));
        Assert.AreEqual(2, calls);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.AreEqual(0, Directory.EnumerateFiles(fixture.Root).Count(file => Path.GetFileName(file).StartsWith("UserKey.bin.", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SoftwareChaChaV0MatchesIndependentZeroKeyStreamAcrossTwoBlocks()
    {
        // Published all-zero ChaCha20 blocks; an all-zero 64-bit/96-bit nonce gives the same initial state.
        // https://sources.debian.org/src/nettle/3.8.1-2/testsuite/chacha-test.c/
        var stream = Convert.FromHexString("76b8e0ada0f13d90405d6ae55386bd28bdd219b8a08ded1aa836efcc8b770dc7da41597c5157488d7724e03fb8d84a376a43b8f41518a11cc387b669b2ee6586"
            + "9f07e7be5551387a98ba977c732d080dcb0f29a048e3656912c6533e32ee7aed29b721769ce64e43d57133b074d839d531ed1f28510afb45ace10a1f4b794d6f");
        var expected = new string('A', 64) + "fixture";
        var plaintext = Encoding.UTF8.GetBytes(expected);
        var payload = new byte[12 + plaintext.Length];
        for (var i = 0; i < plaintext.Length; i++) payload[12 + i] = (byte)(plaintext[i] ^ stream[i]);
        var encoded = Convert.ToBase64String(Pack(0, payload));
        Assert.AreEqual(expected, LegacyFieldDecryptor.Decrypt(encoded, new byte[32]));
        Assert.IsTrue(LegacyFieldDecryptor.UsesUserKey(encoded));
        Assert.IsFalse(LegacyFieldDecryptor.HasAuthenticationTag(encoded));
        payload[12] = (byte)(0xff ^ stream[0]);
        Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.Decrypt(Convert.ToBase64String(Pack(0, payload)), new byte[32]));
    }

    [TestMethod]
    [DataRow(1u)]
    [DataRow(2u)]
    public void AuthenticatedFieldFormatsDecryptAndRejectTamperedTags(uint version)
    {
        // Independent Python cryptography 50.0.2 / OpenSSL fixture. The host OS
        // cannot generate ChaCha20-Poly1305 on Windows 10, which is what this tests.
        var encoded = version == 1
            ? "RU5DIQAAAAEAAABJAwQFBgcICQoLDA0ODxAREgECAwQFBgcICQoLDDiHkwl2H50+BcevX2ntTuUM3h9J2fBart3zJjJNQZzxDioH4nSwI2Njou1Yag=="
            : AuthenticatedField(version, "fake token 中文 with spaces");
        Assert.AreEqual("fake token 中文 with spaces", LegacyFieldDecryptor.Decrypt(encoded, Key));
        Assert.IsTrue(LegacyFieldDecryptor.HasAuthenticationTag(encoded));
        var damaged = Convert.FromBase64String(encoded); damaged[12 + (version == 1 ? 28 : 12)] ^= 1;
        var ex = Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.Decrypt(Convert.ToBase64String(damaged), Key));
        Assert.AreEqual(CredentialFailure.Corrupt, ex.Failure);
        var wrong = Key.ToArray(); wrong[0] ^= 1;
        Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.Decrypt(encoded, wrong));
    }

    [TestMethod]
    public async Task HistoricalCbcUsesExactCpuDerivationWithoutUserKeyFile()
    {
        Assert.AreEqual("FDD3ED6C9B2E44D83954D0D9FBBE9BBB", LegacyFieldDecryptor.HistoricalPasswordFromCpuId(null));
        Assert.AreEqual("162F6A986E2BE99F50F9035CA4855BB9", LegacyFieldDecryptor.HistoricalPasswordFromCpuId("fixture-cpu-identifier"));
        var fixture = new PlatformFixture();
        var missing = fixture.Child("missing UserKey.bin");
        var reader = new WindowsLegacyCredentialReader(missing, (_, _) => throw new InvalidOperationException("Historical CBC must not read a modern user key."),
            () => "fixture-cpu-identifier");
        var encoded = HistoricalCbc("fake historical password 中文", "162F6A986E2BE99F50F9035CA4855BB9");
        Assert.AreEqual("fake historical password 中文", await reader.DecryptFieldAsync(encoded));
        Assert.IsFalse(LegacyFieldDecryptor.HasAuthenticationTag(encoded));
        Assert.IsFalse(File.Exists(missing));
        Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.Decrypt(encoded, historicalPassword: "wrong"));
    }

    [TestMethod]
    public async Task LockedAndFailedKeyReadsNeverRotateCacheOrReplaceTheFile()
    {
        var fixture = new PlatformFixture(); var path = fixture.Child("UserKey.bin");
        var bytes = Pack(1, [1, 2, 3, 4]); File.WriteAllBytes(path, bytes);
        var fail = true;
        var reader = new WindowsLegacyCredentialReader(path, (_, _) => fail ? throw new CryptographicException("synthetic failure") : Key.ToArray());
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<CredentialStoreException>(() => reader.ReadUserKeyAsync());
            Assert.AreEqual(CredentialFailure.IOFailure, ex.Failure);
        }
        var corrupt = await Assert.ThrowsAsync<CredentialStoreException>(() => reader.ReadUserKeyAsync());
        Assert.AreEqual(CredentialFailure.Corrupt, corrupt.Failure);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        fail = false;
        CollectionAssert.AreEqual(Key, (await reader.ReadUserKeyAsync())!);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(0, Directory.EnumerateFiles(fixture.Root).Count(file => Path.GetFileName(file).StartsWith("UserKey.bin.", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task MissingBadAndNewStoreKeysCannotBeTreatedAsLegacySuccess()
    {
        var fixture = new PlatformFixture(); var path = fixture.Child("UserKey.bin");
        var reader = new WindowsLegacyCredentialReader(path, (_, _) => throw new InvalidOperationException("Malformed key must be rejected before unprotect."));
        Assert.IsNull(await reader.ReadUserKeyAsync());
        Assert.IsFalse(File.Exists(path));
        var absent = await Assert.ThrowsAsync<CredentialStoreException>(() => reader.DecryptFieldAsync(AuthenticatedField(2, "fake")));
        Assert.AreEqual(CredentialFailure.Unavailable, absent.Failure);
        var newStore = Pack(2, [1, 2, 3, 4]); BinaryPrimitives.WriteUInt32BigEndian(newStore, 0x53484f55);
        foreach (var invalid in new[] { new byte[3], Pack(0, [1]), Pack(7, [1]), Pack(1, []), Pack(2, [1]).Concat(new byte[] { 0 }).ToArray(), newStore })
        {
            File.WriteAllBytes(path, invalid);
            var ex = await Assert.ThrowsAsync<CredentialStoreException>(() => reader.ReadUserKeyAsync());
            Assert.AreEqual(CredentialFailure.Corrupt, ex.Failure);
            CollectionAssert.AreEqual(invalid, File.ReadAllBytes(path));
        }
        Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.UsesUserKey(Convert.ToBase64String(newStore)));
        Assert.Throws<CredentialStoreException>(() => LegacyFieldDecryptor.Decrypt("not a plaintext fallback"));
        Assert.AreEqual("", await reader.DecryptFieldAsync(null));
        Assert.AreEqual("", await reader.DecryptFieldAsync(""));
    }

    [TestMethod]
    [DataRow(1u)]
    [DataRow(2u)]
    public async Task NativeDpapiReadIsExplicitlyUnavailableOffWindowsAndReadOnlyOnWindows(uint version)
    {
        var fixture = new PlatformFixture(); var path = fixture.Child("UserKey.bin");
        var protectedBytes = new byte[] { 1, 2, 3, 4 };
        if (OperatingSystem.IsWindows())
            protectedBytes = version == 1
                ? ProtectedData.Protect(Key, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser)
                : CngProtectedData.Protect(Key, Entropy, PlainToolkit.CngProtectedData.DataProtectionScope.CurrentUser);
        var bytes = Pack(version, protectedBytes); File.WriteAllBytes(path, bytes);
        var reader = new WindowsLegacyCredentialReader(path);
        if (OperatingSystem.IsWindows()) CollectionAssert.AreEqual(Key, (await reader.ReadUserKeyAsync())!);
        else
        {
            var ex = await Assert.ThrowsAsync<CredentialStoreException>(() => reader.ReadUserKeyAsync());
            Assert.AreEqual(CredentialFailure.Unavailable, ex.Failure);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void LegacyAndNewEntrancesCannotAcquireTheSameLockInEitherOrder()
    {
        var fixture = new PlatformFixture();
        var paths = new AppPaths(new PlatformOptions(fixture.Child("legacy data")), new NativePlatformInfo().Context);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LockFile)!);
        // FileMode.Create is the native Windows contract. Unix fixture uses OpenOrCreate to avoid O_TRUNC-before-flock semantics.
        var legacyMode = OperatingSystem.IsWindows() ? FileMode.Create : FileMode.OpenOrCreate;
        var locks = new DataDirectoryLock();
        using (var old = new FileStream(paths.LockFile, legacyMode, FileAccess.ReadWrite, FileShare.Read))
            Assert.Throws<IOException>(() => locks.Acquire(paths));
        using (var current = locks.Acquire(paths))
            Assert.Throws<IOException>(() => new FileStream(paths.LockFile, legacyMode, FileAccess.ReadWrite, FileShare.Read));
        Assert.IsTrue(File.Exists(paths.LockFile));
        using var recovered = locks.Acquire(paths);
        Assert.AreEqual(paths.LockFile, recovered.LockPath);
    }

    private static byte[] Pack(uint version, byte[] payload)
    {
        var bytes = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x454e4321);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), payload.Length);
        payload.CopyTo(bytes, 12); return bytes;
    }
    private static string AuthenticatedField(uint version, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var tag = new byte[16]; var cipher = new byte[bytes.Length];
        if (version == 2)
        {
            using var aes = new AesGcm(Key, 16); aes.Encrypt(nonce, bytes, cipher, tag);
            return Convert.ToBase64String(Pack(2, nonce.Concat(tag).Concat(cipher).ToArray()));
        }
        var salt = Enumerable.Range(3, 16).Select(i => (byte)i).ToArray();
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, Key, 32, salt, "PCL.Core.Utils.Encryption.ChaCha20"u8.ToArray());
        using var chacha = new ChaCha20Poly1305(derived); chacha.Encrypt(nonce, bytes, cipher, tag);
        CryptographicOperations.ZeroMemory(derived);
        return Convert.ToBase64String(Pack(1, salt.Concat(nonce).Concat(tag).Concat(cipher).ToArray()));
    }
    private static string HistoricalCbc(string value, string password)
    {
        var salt = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var iv = Enumerable.Range(2, 16).Select(i => (byte)i).ToArray();
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 32);
        using var aes = Aes.Create(); aes.Key = derived;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(value), iv, PaddingMode.PKCS7);
        CryptographicOperations.ZeroMemory(derived);
        return Convert.ToBase64String(salt.Concat(iv).Concat(cipher).ToArray());
    }
}
