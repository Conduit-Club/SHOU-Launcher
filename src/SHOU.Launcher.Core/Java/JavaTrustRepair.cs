using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Java;

/// <summary>Fingerprint-based, reversible repairs. Scanning never executes a Java binary.</summary>
public static class JavaTrustRepair
{
    private static readonly object Gate = new();
    private const int MaxSize = 32 * 1024 * 1024;
    private const string ReceiptSuffix = ".shou-trust-repair.json";
    private sealed record Receipt(string Backup, string Before, string After, bool Existed, string Policy);

    public static IReadOnlyList<JavaTrustFinding> ScanJava(string binDirectory, int majorVersion)
    {
        var home = Directory.GetParent(Path.GetFullPath(binDirectory))!.FullName;
        var directories = new[] { Path.Combine(home, "lib", "security"), Path.Combine(home, "jre", "lib", "security") };
        var reports = new List<JavaTrustFinding>();
        foreach (var directory in directories)
        {
            if (!File.Exists(Path.Combine(directory, "cacerts"))) continue;
            reports.AddRange(ScanSecurityDirectory(directory, majorVersion));
        }
        if (reports.Count == 0) reports.Add(new JavaTrustFinding(home, true, "", false, false, "未找到默认证书库，未能检查"));
        return reports;
    }

    public static IReadOnlyList<JavaTrustFinding> ScanSecurityDirectory(string directory, int majorVersion = 17)
    {
        var reports = new List<JavaTrustFinding> { Scan(Path.Combine(directory, "cacerts"), true) };
        if (File.Exists(Path.Combine(directory, "jssecacerts"))) reports.Add(Scan(Path.Combine(directory, "jssecacerts"), true));
        // Java 8 uses blacklisted.certs; later OpenJDK uses blocked.certs. Honor existing vendor files.
        var blockFiles = new[] { "blocked.certs", "blacklisted.certs" }.Select(n => Path.Combine(directory, n)).Where(File.Exists).ToArray();
        if (blockFiles.Length == 0) blockFiles = [Path.Combine(directory, majorVersion <= 8 ? "blacklisted.certs" : "blocked.certs")];
        reports.AddRange(blockFiles.Select(p => Scan(p, false)));
        return reports;
    }

    public static JavaTrustFinding Scan(string path, bool trustStore)
    {
        path = Path.GetFullPath(path);
        try
        {
            EnsureRegularPath(path);
            var data = Read(path, !trustStore);
            var hash = Fingerprint(data);
            if (trustStore)
            {
                var matches = Certificates(data).Where(JavaTrustPolicy.Distrusted.Contains).Distinct().ToArray();
                var repairable = IsJks(data) || FindKeytool(path) is not null;
                var detail = matches.Length == 0 ? "未命中本次事件撤信任证书" : $"受影响：命中 {matches.Length} 张证书\n{string.Join("\n", matches)}";
                if (matches.Length > 0 && !repairable) detail += "\nPKCS12 证书库缺少同一 Java 的 keytool，请更新该 Java。";
                return new(path, true, hash, matches.Length > 0, matches.Length == 0 || repairable, detail, HasReceipt(path, hash));
            }
            var missing = MissingBlocks(data);
            return new(path, false, hash, missing.Length > 0, true,
                missing.Length == 0 ? "已包含本次事件的 16 张中间证书阻止名单" : $"需加固：阻止名单缺少 {missing.Length} 张已泄露中间证书（含交叉签名）", HasReceipt(path, hash));
        }
        catch (Exception ex) { return new(path, trustStore, "", false, false, $"未能检查：{ex.GetType().Name}"); }
    }

    public static JavaTrustFinding Repair(JavaTrustFinding report, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!report.NeedsRepair || !report.CanRepair) throw new InvalidOperationException("此条目不可自动修复。");
            EnsureRegularPath(report.Path);
            var before = Read(report.Path, !report.IsTrustStore);
            if (Fingerprint(before) != report.Hash) throw new IOException("文件在扫描后发生变化，请重新扫描。");
            byte[] after;
            if (report.IsTrustStore)
            {
                after = IsJks(before) ? JksTrustStore.Read(before).Remove(JavaTrustPolicy.Distrusted) : RepairPkcs12(report.Path, before, cancellationToken);
                if (Certificates(after).Any(JavaTrustPolicy.Distrusted.Contains)) throw new InvalidDataException("修复后的证书库验证失败。");
                var retainedBefore = Certificates(before).Where(f => !JavaTrustPolicy.Distrusted.Contains(f)).Order().ToArray();
                if (!retainedBefore.SequenceEqual(Certificates(after).Order())) throw new InvalidDataException("修复改变了其他证书，已取消写入。");
            }
            else
            {
                after = RepairedBlocks(before);
                if (MissingBlocks(after).Length != 0) throw new InvalidDataException("阻止名单验证失败。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceWithBackup(report.Path, before, after);
            return Scan(report.Path, report.IsTrustStore);
        }
    }

    public static JavaTrustFinding Restore(JavaTrustFinding report)
    {
        lock (Gate)
        {
            EnsureRegularPath(report.Path);
            var receiptPath = report.Path + ReceiptSuffix;
            EnsureRegularPath(receiptPath);
            var receipt = JsonSerializer.Deserialize<Receipt>(Read(receiptPath, false)) ?? throw new InvalidDataException("备份记录无效。");
            if (receipt.Policy != JavaTrustPolicy.Id || Path.GetFileName(receipt.Backup) != receipt.Backup ||
                !receipt.Backup.StartsWith(Path.GetFileName(report.Path) + ".shou-backup-", StringComparison.Ordinal))
                throw new InvalidDataException("备份路径无效。");
            var backup = Path.Combine(Path.GetDirectoryName(report.Path)!, receipt.Backup);
            EnsureRegularPath(backup);
            var original = Read(backup, false);
            if (Fingerprint(original) != receipt.Before) throw new InvalidDataException("备份校验失败，未恢复。");
            var current = Read(report.Path, false);
            if (Fingerprint(current) != receipt.After || report.Hash != receipt.After)
                throw new IOException("证书文件在修复后已变化，不能覆盖厂商或用户的后续更新。");
            DerivedJavaTrustService.InvalidateForRestore(report.Path);
            if (receipt.Existed) WriteAtomic(report.Path, current, original);
            else
            {
                if (original.Length != 0) throw new InvalidDataException("新建文件的备份应为空。");
                using var locked = new FileStream(report.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (locked.Length > MaxSize) throw new IOException("文件在检查后发生变化。");
                using var memory = new MemoryStream(); locked.CopyTo(memory);
                if (Fingerprint(memory.ToArray()) != receipt.After) throw new IOException("文件在检查后发生变化。");
                File.Delete(report.Path);
            }
            return Scan(report.Path, report.IsTrustStore);
        }
    }

    /// <summary>Only traverses the completed download destination, never follows junctions or symbolic links.</summary>
    public static IReadOnlyList<JavaTrustFinding> RepairDownload(string root, bool enabled)
    {
        if (!enabled) return [];
        EnsureRegularPath(root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var stores = Directory.EnumerateFiles(root, "cacerts", options)
            .Where(p => string.Equals(new DirectoryInfo(Path.GetDirectoryName(p)!).Name, "security", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (stores.Length == 0) throw new InvalidDataException("下载目录中未找到 Java 证书库，不能完成自动检查。");
        var results = new List<JavaTrustFinding>();
        foreach (var store in stores)
        {
            var directory = Path.GetDirectoryName(store)!;
            // JDK 8's runtime lives in jre/lib/security. Existing vendor blacklist wins.
            var home = Path.GetFullPath(Path.Combine(directory, "..", ".."));
            var versionFile = Path.Combine(home, "release");
            var javaExe = Path.Combine(home, "bin", "java.exe");
            var major = directory.Contains(Path.DirectorySeparatorChar + "jre" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? 8 : 17;
            if (File.Exists(javaExe))
            {
                var version = FileVersionInfo.GetVersionInfo(javaExe);
                if (version.FileMajorPart > 0) major = version.FileMajorPart == 1 ? version.FileMinorPart : version.FileMajorPart;
            }
            else if (File.Exists(versionFile))
            {
                EnsureRegularPath(versionFile);
                var match = Regex.Match(Encoding.UTF8.GetString(Read(versionFile, false)), "(?m)^JAVA_VERSION=\"(?:1\\.)?(\\d+)");
                if (match.Success) major = int.Parse(match.Groups[1].Value);
            }
            foreach (var report in ScanSecurityDirectory(directory, major))
            {
                if (!report.NeedsRepair || !report.CanRepair) { results.Add(report); continue; }
                try { results.Add(Repair(report)); }
                catch (Exception ex) { results.Add(report with { CanRepair = false, Details = "修复失败：" + ex.GetType().Name }); }
            }
        }
        return results;
    }

    private static bool HasReceipt(string path, string hash)
    {
        try
        {
            var receiptPath = path + ReceiptSuffix;
            EnsureRegularPath(receiptPath);
            var receipt = JsonSerializer.Deserialize<Receipt>(Read(receiptPath, false));
            return receipt?.After == hash;
        }
        catch { return false; }
    }
    private static bool IsJks(byte[] data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0xfeedfeed;
    private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string[] Certificates(byte[] data)
    {
        if (IsJks(data))
        {
            var entries = JksTrustStore.Read(data).Entries;
            if (entries.Any(e => e.HasDistrustedKeyCertificate)) throw new InvalidDataException("私钥条目包含本次事件证书，请使用 Java 厂商工具处理，未自动修改。");
            return entries.Where(e => e.Fingerprint is not null).Select(e => e.Fingerprint!).ToArray();
        }
        // JDK-8275252: shipped cacerts may be plaintext public-certificate PKCS12 without MacData.
        return Pkcs12TrustReader.ReadFingerprints(data, allowUnprotectedCertificateBags: true).ToArray();
    }

    private static string[] MissingBlocks(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        // Fail closed on unfamiliar Java properties rather than replace a vendor blacklist.
        if (data.Length != 0 && (Regex.Matches(text, @"(?m)^\s*Algorithm\b").Count != 1 ||
            !Regex.IsMatch(text, @"(?m)^\s*Algorithm\s*[=:]\s*SHA-256\s*$") || text.Contains('\\')))
            throw new InvalidDataException("阻止名单不是受支持的 SHA-256 格式，请更新 Java；原文件已保留。");
        return JavaTrustPolicy.Compromised.Where(f => !Regex.IsMatch(text, @"(?m)^\s*" + f + @"\s*(?:[=:].*)?$" )).ToArray();
    }

    private static byte[] RepairedBlocks(byte[] before)
    {
        var missing = MissingBlocks(before);
        var prefix = before.Length == 0 ? "Algorithm=SHA-256\n" : "";
        return before.Concat(Encoding.ASCII.GetBytes($"\n{prefix}# SHOU {JavaTrustPolicy.Id}\n{string.Join("\n", missing.Select(f => f + "="))}\n")).ToArray();
    }

    internal static void ValidateTransformation(byte[] before, byte[] after, bool trustStore)
    {
        if (trustStore)
        {
            var expected = Certificates(before).Where(f => !JavaTrustPolicy.Distrusted.Contains(f)).Order().ToArray();
            var actual = Certificates(after).Order().ToArray();
            if (actual.Any(JavaTrustPolicy.Distrusted.Contains) || !expected.SequenceEqual(actual)
                || (IsJks(before) && !JksTrustStore.Read(before).Remove(JavaTrustPolicy.Distrusted).AsSpan().SequenceEqual(after)))
                throw new InvalidDataException("派生证书库不满足精确撤信任策略。");
        }
        else if (!RepairedBlocks(before).AsSpan().SequenceEqual(after))
            throw new InvalidDataException("派生阻止名单不是保留原内容的精确加固结果。");
    }

    internal static string ValidateReceipt(string path, byte[] before, byte[] after, bool existed)
    {
        var receiptPath = path + ReceiptSuffix;
        EnsureRegularPath(receiptPath);
        var receipt = JsonSerializer.Deserialize<Receipt>(Read(receiptPath, false)) ?? throw new InvalidDataException("备份记录无效。");
        if (receipt.Policy != JavaTrustPolicy.Id || receipt.Before != Fingerprint(before)
            || receipt.After != Fingerprint(after) || receipt.Existed != existed
            || Path.GetFileName(receipt.Backup) != receipt.Backup
            || !Regex.IsMatch(receipt.Backup, "^" + Regex.Escape(Path.GetFileName(path)) + @"\.shou-backup-[0-9a-f]{32}$"))
            throw new InvalidDataException("派生备份记录不匹配。");
        var backup = Path.Combine(Path.GetDirectoryName(path)!, receipt.Backup);
        EnsureRegularPath(backup);
        if (!Read(backup, false).AsSpan().SequenceEqual(before)) throw new InvalidDataException("派生备份不匹配。");
        return backup;
    }

    public static void EnsureRegularPath(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            try { if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("文件或上层目录是链接/联接点，请通过系统包管理器或 Java 厂商更新。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static byte[] Read(string path, bool allowMissing)
    {
        if (!File.Exists(path) && allowMissing) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxSize) throw new InvalidDataException("证书文件过大。");
        using var memory = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxSize + 1L - memory.Length))) > 0)
        {
            memory.Write(buffer, 0, count);
            if (memory.Length > MaxSize) throw new InvalidDataException("证书文件增长超过限制。");
        }
        return memory.ToArray();
    }

    private static void ReplaceWithBackup(string path, byte[] before, byte[] after)
    {
        var backup = path + ".shou-backup-" + Guid.NewGuid().ToString("N");
        EnsureRegularPath(path + ReceiptSuffix);
        using (var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { PrivateMode(backup); file.Write(before); file.Flush(true); }
        var receipt = new Receipt(Path.GetFileName(backup), Fingerprint(before), Fingerprint(after), File.Exists(path), JavaTrustPolicy.Id);
        // Save recovery information BEFORE touching the store. A failed replace leaves the original intact.
        var receiptPath = path + ReceiptSuffix;
        WriteAtomic(receiptPath, File.Exists(receiptPath) ? Read(receiptPath, false) : [],
            JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true }));
        PrivateMode(receiptPath);
        WriteAtomic(path, before, after);
    }

    private static void WriteAtomic(string path, byte[] expected, byte[] data)
    {
        EnsureRegularPath(path);
        var temp = path + ".shou-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { PrivateMode(temp); file.Write(data); file.Flush(true); }
            if (File.Exists(path) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            if (File.Exists(path))
            {
                // Deny concurrent writers while checking the snapshot and atomically replacing the file.
                using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (current.Length > MaxSize) throw new IOException("文件在检查后发生变化。");
                using var memory = new MemoryStream(); current.CopyTo(memory);
                if (!memory.ToArray().AsSpan().SequenceEqual(expected)) throw new IOException("文件在检查后发生变化，请重新扫描。");
                File.Replace(temp, path, null);
            }
            else
            {
                if (expected.Length != 0) throw new IOException("原文件已被移除。");
                File.Move(temp, path, false);
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string? FindKeytool(string store)
    {
        var home = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(store)!, "..", ".."));
        var tool = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");
        // Never reach outside a copied JavaHome to a system/user JDK or a PATH fallback.
        return File.Exists(tool) ? tool : null;
    }

    private static byte[] RepairPkcs12(string path, byte[] data, CancellationToken cancellationToken)
    {
        var tool = FindKeytool(path) ?? throw new InvalidOperationException("找不到同一 Java 的 keytool。");
        EnsureRegularPath(tool);
        var temp = path + ".shou-p12-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, data);
            PrivateMode(temp);
            var listing = Keytool(tool, temp, cancellationToken, "-list", "-rfc");
            var aliases = new List<string>();
            foreach (var section in Regex.Split(listing, @"(?m)^Alias name: ").Skip(1))
            {
                var alias = section.Split('\n')[0].TrimEnd('\r');
                var pem = Regex.Match(section, "-----BEGIN CERTIFICATE-----[\\s\\S]*?-----END CERTIFICATE-----");
                if (!pem.Success) continue;
                using var cert = X509Certificate2.CreateFromPem(pem.Value);
                if (JavaTrustPolicy.Distrusted.Contains(cert.GetCertHashString(HashAlgorithmName.SHA256))) aliases.Add(alias);
            }
            if (aliases.Count == 0) throw new InvalidDataException("keytool 未能定位目标证书，未修改原文件。");
            foreach (var alias in aliases) Keytool(tool, temp, cancellationToken, "-delete", "-alias", alias);
            return Read(temp, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Keytool(string tool, string file, CancellationToken cancellationToken, params string[] arguments) =>
        RunKeytoolAsync(tool, file, arguments, cancellationToken).GetAwaiter().GetResult();

    private static async Task<string> RunKeytoolAsync(string tool, string file, string[] arguments, CancellationToken ct)
    {
        EnsureRegularPath(tool);
        JavaTrustTree.EnsureSingleRegularFile(tool);
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = Path.GetDirectoryName(file)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("PCL_", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("CURSEFORGE_", StringComparison.OrdinalIgnoreCase)
                     || new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" }.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(tool))!;
        start.Environment["SHOU_TRUST_PASSWORD"] = "changeit";
        foreach (var arg in new[] { "-J-XX:-UsePerfData", "-J-Djava.io.tmpdir=" + Path.GetDirectoryName(file),
                     "-J-Duser.language=en", "-J-Duser.country=US", "-J-Dfile.encoding=UTF-8",
                     "-J-Dsun.stdout.encoding=UTF-8", "-J-Dsun.stderr.encoding=UTF-8",
                     "-keystore", file, "-storepass:env", "SHOU_TRUST_PASSWORD" }.Concat(arguments)) start.ArgumentList.Add(arg);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动 keytool。");
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var output = CaptureAsync(process.StandardOutput, linked.Token);
        var errors = CaptureAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).WaitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("KeytoolFailedExit");
            if (output.Result.Truncated || errors.Result.Truncated) throw new IOException("KeytoolOutputLimit");
            return output.Result.Text;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("KeytoolTimeout");
        }
        finally
        {
            linked.Cancel();
            _ = Task.WhenAll(output, errors).ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 8 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[8192]; var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var retained = Math.Min(count, limit - text.Length);
            if (retained > 0) text.Append(buffer, 0, retained);
            truncated |= retained != count;
        }
        return (text.ToString(), truncated);
    }

    private static void PrivateMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
