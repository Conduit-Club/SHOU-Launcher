using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Java;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class JavaTrustRepairTest
{
    private string _root = null!;
    private string Store => Path.Combine(_root, "lib", "security", "cacerts");
    [TestInitialize] public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "SHOU-Trust-Test", Guid.NewGuid().ToString("N"));
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
        StringAssert.Contains(report.Details, "校验失败");
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
    public void DirectoryJunctionsAreNotFollowedOrModified()
    {
        var target = Path.Combine(_root, "external"); Directory.CreateDirectory(target);
        var original = Jks(2, [("root", RootCertificate())]);
        File.WriteAllBytes(Path.Combine(target, "cacerts"), original);
        // Windows directory junctions do not require administrator or developer mode.
        var link = Path.Combine(Path.GetDirectoryName(Store)!, "linked");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        process.WaitForExit(); Assert.AreEqual(0, process.ExitCode);
        var report = JavaTrustRepair.Scan(Path.Combine(link, "cacerts"), true);
        Assert.IsFalse(report.CanRepair);
        StringAssert.Contains(report.Details, "链接");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(target, "cacerts")));
    }

    [TestMethod]
    public void Pkcs12IsInspectedWithoutExecutingJavaAndMissingKeytoolIsReported()
    {
        using var cert = X509CertificateLoader.LoadCertificate(RootCertificate());
        File.WriteAllBytes(Store, cert.Export(X509ContentType.Pkcs12, "changeit"));
        var report = JavaTrustRepair.Scan(Store, true);
        Assert.IsTrue(report.NeedsRepair);
        Assert.IsFalse(report.CanRepair);
        StringAssert.Contains(report.Details, "keytool");
    }

    [TestMethod]
    [TestCategory("JavaInterop")]
    public void RealKeytoolReadsRepairedJksAndRepairsPkcs12InAnIsolatedRuntimeCopy()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME_17_X64") ?? Environment.GetEnvironmentVariable("JAVA_HOME");
        if (javaHome is null || !File.Exists(Path.Combine(javaHome, "bin", "keytool.exe")))
            Assert.Inconclusive("需要已安装的 JDK（JAVA_HOME 或 JAVA_HOME_17_X64）运行互操作测试。");
        JavaTrustRepair.EnsureRegularPath(javaHome);
        // Copy the runtime: the original JDK and its trust store are never modified.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var source in Directory.EnumerateFiles(javaHome, "*", options))
        {
            var destination = Path.Combine(_root, Path.GetRelativePath(javaHome, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
        }
        var tool = Path.Combine(_root, "bin", "keytool.exe");
        string Run(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var variable in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" }) start.Environment.Remove(variable);
            foreach (var arg in new[] { "-J-Duser.language=en", "-keystore", Store, "-storepass", "changeit" }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            Assert.IsTrue(process.WaitForExit(30000));
            var result = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            Assert.AreEqual(0, process.ExitCode, result); return result;
        }
        File.WriteAllBytes(Store, Jks(2, [("root", RootCertificate()), ("safe", SafeCertificate())]));
        JavaTrustRepair.Repair(JavaTrustRepair.Scan(Store, true));
        StringAssert.Contains(Run("-list"), "Your keystore contains 1 entry");
        var rootPem = Path.Combine(_root, "root.pem"); File.WriteAllText(rootPem, PemEncoding.WriteString("CERTIFICATE", RootCertificate()));
        // Move the synthetic JKS aside; keytool creates a genuine PKCS12 with trusted-cert bag attributes.
        File.Move(Store, Store + ".tested-jks");
        Run("-importcert", "-noprompt", "-storetype", "PKCS12", "-alias", "arbitrary-root", "-file", rootPem);
        var safePem = Path.Combine(_root, "safe.pem"); File.WriteAllText(safePem, PemEncoding.WriteString("CERTIFICATE", SafeCertificate()));
        Run("-importcert", "-noprompt", "-alias", "safe", "-file", safePem);
        var before = File.ReadAllBytes(Store);
        var affected = JavaTrustRepair.Scan(Store, true);
        Assert.IsTrue(affected.CanRepair && affected.NeedsRepair);
        var repaired = JavaTrustRepair.Repair(affected);
        Assert.IsFalse(repaired.NeedsRepair);
        StringAssert.Contains(Run("-list"), "Your keystore contains 1 entry");
        JavaTrustRepair.Restore(repaired);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Store));
        StringAssert.Contains(Run("-list"), "Your keystore contains 2 entries");
    }

    private static byte[] RootCertificate()
    {
        using var resource = typeof(JavaTrustRepairTest).Assembly.GetManifestResourceStream("Conduit.emsign-g3.pem")!;
        using var reader = new StreamReader(resource);
        using var cert = X509Certificate2.CreateFromPem(reader.ReadToEnd()); return cert.RawData;
    }

    private static byte[] SafeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=emSign ECC Root CA - G3", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
        return cert.RawData;
    }

    private static byte[] Jks(int version, (string alias, byte[] cert)[] entries, bool privateKey = false)
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
}
