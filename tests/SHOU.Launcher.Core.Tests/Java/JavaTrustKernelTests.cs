using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
[TestCategory("JavaTrust")]
public sealed class JavaTrustKernelTests
{
    private string _root = null!;
    private string Store => Path.Combine(_root, "lib", "security", "cacerts");
    [TestInitialize] public void Init()
    {
        _root = new PlatformFixture().Child("trust-kernel");
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
    }

    [TestMethod]
    public void PolicyUsesVerifiedFingerprintsAndDoesNotDistrustAllEmSignRoots()
    {
        Assert.AreEqual(JavaTrustPolicy.Root, Convert.ToHexString(SHA256.HashData(RootCertificate())));
        Assert.HasCount(16, JavaTrustPolicy.Compromised.Distinct().ToArray());
        Assert.IsTrue(JavaTrustPolicy.Distrusted.All(f => f.Length == 64 && f.All(Uri.IsHexDigit)));
        Assert.IsFalse(JavaTrustPolicy.Distrusted.Contains("CEF71E70B7C29ADDF6C30CD19E614B38FD5F02A435A0EEDDD0087E183D101A51"));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RepairsExactCertificateAcrossArbitraryAliasesAndPreservesOtherEntries(int version)
    {
        var safe = SafeCertificate();
        var before = Jks(version, [("unknown-alias", RootCertificate()), ("emsign-root-name-but-safe", safe), ("duplicate-root", RootCertificate())], true);
        File.WriteAllBytes(Store, before);
        var retained = JksTrustStore.Read(before).Entries.Where(e => e.Fingerprint != JavaTrustPolicy.Root).Select(e => e.Encoded).ToArray();
        var report = JavaTrustRepair.Scan(Store, true);
        Assert.IsTrue(report.NeedsRepair);
        var fixedReport = JavaTrustRepair.Repair(report);
        Assert.IsFalse(fixedReport.NeedsRepair);
        Assert.IsTrue(fixedReport.CanRestore);
        var entries = JksTrustStore.Read(File.ReadAllBytes(Store)).Entries;
        Assert.HasCount(2, entries); // Safe root and encrypted private-key entry, unchanged.
        for (var i = 0; i < retained.Length; i++) CollectionAssert.AreEqual(retained[i], entries[i].Encoded);
        var restored = JavaTrustRepair.Restore(fixedReport);
        Assert.IsTrue(restored.NeedsRepair);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Store));
        Assert.Throws<IOException>(() => JavaTrustRepair.Restore(fixedReport));
    }

    [TestMethod]
    public void SameNameCertificateDoesNotMatch()
    {
        File.WriteAllBytes(Store, Jks(2, [("emsign-ecc-root-ca-g3", SafeCertificate())]));
        Assert.IsFalse(JavaTrustRepair.Scan(Store, true).NeedsRepair);
    }

    [TestMethod]
    public void CorruptionAndCustomPasswordAreReportedWithoutWriting()
    {
        var corrupt = Jks(2, [("root", RootCertificate())]); corrupt[^1] ^= 1;
        File.WriteAllBytes(Store, corrupt);
        var report = JavaTrustRepair.Scan(Store, true);
        Assert.IsFalse(report.CanRepair);
        StringAssert.Contains(report.Details, nameof(InvalidDataException));
        Assert.Throws<InvalidOperationException>(() => JavaTrustRepair.Repair(report));
        CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(Store));
        Assert.IsFalse(File.Exists(Store + ".shou-trust-repair.json"));
    }

    [TestMethod]
    public void RejectsTruncatedAndUnsupportedJksEvenWithValidDigest()
    {
        var bytes = Jks(2, [("root", RootCertificate())]);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 100000); Sign(bytes);
        Assert.Throws<InvalidDataException>(() => JksTrustStore.Read(bytes));
        bytes = Jks(2, []); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 42); Sign(bytes);
        Assert.Throws<InvalidDataException>(() => JksTrustStore.Read(bytes));
    }

    [TestMethod]
    public void ScanAndRestoreRejectConcurrentChanges()
    {
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate())]));
        var report = JavaTrustRepair.Scan(Store, true);
        var changed = Jks(2, [("vendor-update", SafeCertificate())]);
        File.WriteAllBytes(Store, changed);
        Assert.Throws<IOException>(() => JavaTrustRepair.Repair(report));
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate())]));
        var repaired = JavaTrustRepair.Repair(JavaTrustRepair.Scan(Store, true));
        File.WriteAllBytes(Store, changed);
        Assert.Throws<IOException>(() => JavaTrustRepair.Restore(repaired));
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(Store));
    }

    [TestMethod]
    public void BackupTamperingNeverOverwritesRepairedFile()
    {
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate())]));
        var report = JavaTrustRepair.Repair(JavaTrustRepair.Scan(Store, true));
        var repaired = File.ReadAllBytes(Store);
        var backup = Directory.GetFiles(Path.GetDirectoryName(Store)!, "cacerts.shou-backup-*").Single();
        File.WriteAllText(backup, "modified");
        Assert.Throws<InvalidDataException>(() => JavaTrustRepair.Restore(report));
        CollectionAssert.AreEqual(repaired, File.ReadAllBytes(Store));
    }

    [TestMethod]
    public void BlacklistAppendsAllOriginalAndCrossSignedCertificatesAndPreservesVendorData()
    {
        var path = Path.Combine(Path.GetDirectoryName(Store)!, "blocked.certs");
        var original = Encoding.ASCII.GetBytes("# vendor entries\r\nAlgorithm=SHA-256\r\n" + new string('A', 64) + "=\r\n");
        File.WriteAllBytes(path, original);
        var report = JavaTrustRepair.Scan(path, false);
        var repaired = JavaTrustRepair.Repair(report);
        Assert.IsFalse(repaired.NeedsRepair);
        var after = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(original, after[..original.Length]);
        foreach (var hash in JavaTrustPolicy.Compromised) StringAssert.Contains(Encoding.ASCII.GetString(after), hash + "=");
        Assert.Throws<InvalidOperationException>(() => JavaTrustRepair.Repair(repaired));
        JavaTrustRepair.Restore(repaired);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("Algorithm=SHA-1\n")]
    [DataRow("Algorithm=SHA-256\nAlgorithm=SHA-1\n")]
    [DataRow("Algorithm=SHA-256\n# continuation\\\n")]
    public void UnsupportedBlacklistRemainsUntouched(string contents)
    {
        var path = Path.Combine(Path.GetDirectoryName(Store)!, "blocked.certs");
        File.WriteAllText(path, contents);
        var report = JavaTrustRepair.Scan(path, false);
        Assert.IsFalse(report.CanRepair);
        Assert.Throws<InvalidOperationException>(() => JavaTrustRepair.Repair(report));
        Assert.AreEqual(contents, File.ReadAllText(path));
    }

    [TestMethod]
    public void DownloadRepairFinishesBeforeReportingReadyAndIsIdempotent()
    {
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate()), ("safe", SafeCertificate())]));
        var results = JavaTrustRepair.RepairDownload(_root, true);
        Assert.IsTrue(results.All(r => r.CanRepair && !r.NeedsRepair));
        var backupCount = Directory.GetFiles(Path.GetDirectoryName(Store)!, "*.shou-backup-*").Length;
        var again = JavaTrustRepair.RepairDownload(_root, true);
        Assert.IsTrue(again.All(r => !r.NeedsRepair));
        Assert.AreEqual(backupCount, Directory.GetFiles(Path.GetDirectoryName(Store)!, "*.shou-backup-*").Length);
    }

    [TestMethod]
    public void DisabledAutoRepairDoesNotInspectOrWrite()
    {
        Assert.IsEmpty(JavaTrustRepair.RepairDownload(Path.Combine(_root, "missing"), false));
        Assert.Throws<InvalidDataException>(() => JavaTrustRepair.RepairDownload(_root, true));
    }

    [TestMethod]
    public void RestoringANewBlacklistReturnsToItsOriginalAbsence()
    {
        var path = Path.Combine(Path.GetDirectoryName(Store)!, "blocked.certs");
        var repaired = JavaTrustRepair.Repair(JavaTrustRepair.Scan(path, false));
        Assert.IsTrue(File.Exists(path));
        var restored = JavaTrustRepair.Restore(repaired);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(restored.CanRestore);
    }

    [TestMethod]
    public void Java8AndJssecacertsAreDiscovered()
    {
        var directory = Path.GetDirectoryName(Store)!;
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate())]));
        File.Copy(Store, Path.Combine(directory, "jssecacerts"));
        var reports = JavaTrustRepair.ScanJava(Path.Combine(_root, "bin"), 8);
        Assert.HasCount(3, reports);
        Assert.HasCount(2, reports.Where(r => r.IsTrustStore && r.NeedsRepair).ToArray());
        Assert.IsTrue(reports.Any(r => r.Path.EndsWith("blacklisted.certs")));
        File.WriteAllText(Path.Combine(_root, "release"), "JAVA_VERSION=\"1.8.0_432\"\n");
        JavaTrustRepair.RepairDownload(_root, true);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "blacklisted.certs")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "blocked.certs")));
    }

    [TestMethod]
    public void Pkcs12IsInspectedWithoutExecutingJavaAndMissingKeytoolIsReported()
    {
        using var cert = X509CertificateLoader.LoadCertificate(RootCertificate());
        File.WriteAllBytes(Store, cert.Export(X509ContentType.Pkcs12, "changeit"));
        var report = JavaTrustRepair.Scan(Store, true);
        Assert.IsTrue(report.NeedsRepair, report.Details);
        Assert.IsFalse(report.CanRepair);
        StringAssert.Contains(report.Details, "keytool");
    }

    [TestMethod]
    public void Pkcs12PrivateUnknownBagsBadMacAndTrailingBytesAreRejectedWithoutImportingKeys()
    {
        using var cert = X509CertificateLoader.LoadCertificate(RootCertificate());
        using var key = RSA.Create(2048);
        foreach (var kind in new[] { "key", "secret", "nested", "bad-mac", "trailing" })
        {
            var safe = new Pkcs12SafeContents(); safe.AddCertificate(cert);
            if (kind == "key") safe.AddKeyUnencrypted(key);
            if (kind == "secret") safe.AddSecret(new Oid("1.2.3.4"), new byte[] { 0x05, 0x00 });
            if (kind == "nested") safe.AddNestedContents(new Pkcs12SafeContents());
            var builder = new Pkcs12Builder(); builder.AddSafeContentsUnencrypted(safe); builder.SealWithMac("changeit", HashAlgorithmName.SHA256, 1000);
            var bytes = builder.Encode();
            if (kind == "bad-mac") bytes[^1] ^= 1;
            if (kind == "trailing") bytes = bytes.Concat(new byte[] { 0 }).ToArray();
            File.WriteAllBytes(Store, bytes);
            var finding = JavaTrustRepair.Scan(Store, true);
            Assert.IsFalse(finding.CanRepair, kind);
            Assert.IsFalse(finding.NeedsRepair, "Malformed/private-key input must fail parsing, not merely miss keytool: " + kind);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Store));
            Assert.IsFalse(File.Exists(Store + ".shou-trust-repair.json"));
        }
    }

    internal static byte[] RootCertificate()
    {
        using var cert = X509Certificate2.CreateFromPem(RootPem); return cert.RawData;
    }

    internal static byte[] SafeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=emSign ECC Root CA - G3", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
        return cert.RawData;
    }

    internal static byte[] Jks(int version, (string alias, byte[] cert)[] entries, bool privateKey = false)
    {
        using var stream = new MemoryStream();
        void Int(int n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, n); stream.Write(b); }
        void Utf(string s) { var b = Encoding.UTF8.GetBytes(s); Span<byte> n = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(n, (ushort)b.Length); stream.Write(n); stream.Write(b); }
        Int(unchecked((int)0xfeedfeed)); Int(version); Int(entries.Length + (privateKey ? 1 : 0));
        foreach (var (alias, cert) in entries)
        {
            Int(2); Utf(alias); stream.Write(new byte[8]); if (version == 2) Utf("X.509"); Int(cert.Length); stream.Write(cert);
        }
        if (privateKey) { Int(1); Utf("preserve-key"); stream.Write(new byte[8]); Int(3); stream.Write(new byte[] { 1, 2, 3 }); Int(0); }
        stream.Write(new byte[20]); var bytes = stream.ToArray(); Sign(bytes); return bytes;
    }

    private static void Sign(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.BigEndianUnicode.GetBytes("changeit")); hash.AppendData("Mighty Aphrodite"u8); hash.AppendData(data.AsSpan(0, data.Length - 20));
        hash.GetHashAndReset().CopyTo(data, data.Length - 20);
    }
    internal const string RootPem = """
-----BEGIN CERTIFICATE-----
MIICTjCCAdOgAwIBAgIKPPYHqWhwDtqLhDAKBggqhkjOPQQDAzBrMQswCQYDVQQG
EwJJTjETMBEGA1UECxMKZW1TaWduIFBLSTElMCMGA1UEChMcZU11ZGhyYSBUZWNo
bm9sb2dpZXMgTGltaXRlZDEgMB4GA1UEAxMXZW1TaWduIEVDQyBSb290IENBIC0g
RzMwHhcNMTgwMjE4MTgzMDAwWhcNNDMwMjE4MTgzMDAwWjBrMQswCQYDVQQGEwJJ
TjETMBEGA1UECxMKZW1TaWduIFBLSTElMCMGA1UEChMcZU11ZGhyYSBUZWNobm9s
b2dpZXMgTGltaXRlZDEgMB4GA1UEAxMXZW1TaWduIEVDQyBSb290IENBIC0gRzMw
djAQBgcqhkjOPQIBBgUrgQQAIgNiAAQjpQy4LRL1KPOxst3iAhKAnjlfSU2fySU0
WXTsuwYc58Byr+iuL+FBVIcUqEqy6HyC5ltqtdyzdc6LBtCGI79G1Y4PPwT01xyS
fvalY8L1X44uT6EYGQIrMgqCZH0Wk9GjQjBAMB0GA1UdDgQWBBR8XQKEE9TMipuB
zhccLikenEhjQjAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0TAQH/BAUwAwEB/zAKBggq
hkjOPQQDAwNpADBmAjEAvvNhzwIQHWSVB7gYboiFBS+DCBeQyh+KTOgNG3qxrdWB
CUfvO6wIBHxcmbHtRwfSAjEAnbpV/KlK6O3t5nYBQnvI+GDZjVGLVTv7jHvrZQnD
+JbNR6iC8hZVdyR+EhCVBCyj
-----END CERTIFICATE-----
""";

}
