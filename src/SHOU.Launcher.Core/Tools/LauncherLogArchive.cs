using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Diagnostics;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Tools;

public sealed record HistoricalLauncherLog(string Name, long Bytes, string Sha256);
public static class LauncherLogArchive
{
    private static readonly LogRedactor Redactor = new();
    private static bool Allowed(FileInfo f) => f.Extension is ".log" or ".txt" && f.Name != "launcher.log" && !f.Name.StartsWith("game-", StringComparison.OrdinalIgnoreCase);
    private static string LiteralFile(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains(':') || name.IndexOfAny(['/', '\\']) >= 0) throw new InvalidDataException("日志文件名无效。");
        var file = Path.Combine(Path.GetFullPath(directory), name); VerifiedDownloader.RejectLink(file); return file;
    }
    public static async Task<byte[]> ExportAsync(string directory, CancellationToken ct = default)
    {
        VerifiedDownloader.RejectLink(directory); using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            if (Directory.Exists(directory))
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles().Where(f => f.Extension is ".log" or ".txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(100))
                {
                    ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(file.FullName);
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, true);
                    var truncated = input.Length > 1024 * 1024;
                    var limit = (int)Math.Min(input.Length, 1024 * 1024); if (truncated) input.Seek(-limit, SeekOrigin.End);
                    var bytes = new byte[limit]; await input.ReadExactlyAsync(bytes, ct);
                    using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true); var text = await reader.ReadToEndAsync(ct);
                    if (truncated) { var newline = text.IndexOf('\n'); text = newline < 0 ? "" : text[(newline + 1)..]; }
                    if (text.Contains('\0')) text = "[Unsupported log encoding omitted.]";
                    var clean = string.Join('\n', text.Split('\n').Select(line => Redactor.Scrub(line)));
                    clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?token|client[_-]?secret|api[_-]?key|token|secret|password|passwd|rpc[_-]?secret|authorization|code_verifier)\b[""']?\s*[:=]\s*[""']?)(?:Bearer\s+)?[^\s,""';&]+", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)(cookie|set-cookie|api[_-]?key)(\s*[:=]\s*)[^\r\n]+", "$1$2<redacted>", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)https?://[^\s\""<>]+", m => Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path) : "<redacted-url>", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (home.Length > 0) clean = clean.Replace(home, "<home>", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    if (truncated) clean = "[Earlier content omitted; exporting the last 1 MiB.]\n" + clean;
                    var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal); await using var stream = entry.Open();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(clean), ct);
                }
            var readme = zip.CreateEntry("README.txt"); await using var notes = readme.Open();
            await notes.WriteAsync(Encoding.UTF8.GetBytes("Sanitized launcher logs only. Up to 100 files, the last 1 MiB of each (truncation is marked). Account caches, configuration and game files are excluded. Review before sharing."), ct);
        }
        return result.ToArray();
    }
    public static async Task<IReadOnlyList<HistoricalLauncherLog>> ScanAsync(string directory, CancellationToken ct = default)
    {
        VerifiedDownloader.RejectLink(directory); List<HistoricalLauncherLog> entries = [];
        if (!Directory.Exists(directory)) return entries;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles().Where(Allowed).Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)).Take(100))
        {
            ct.ThrowIfCancellationRequested(); if (file.LinkTarget is not null || file.Length > 10 * 1024 * 1024) continue;
            await using var read = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            entries.Add(new(file.Name, read.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct))));
        }
        return entries;
    }
    public static async Task<string> RecycleAsync(string directory, IReadOnlyList<HistoricalLauncherLog> plan, CancellationToken ct = default)
    {
        if (plan.Count > 100 || plan.Any(p => p is null || string.IsNullOrEmpty(p.Sha256) || p.Sha256.Length != 64 || !p.Sha256.All(Uri.IsHexDigit)) || plan.Select(p => p.Name).Distinct().Count() != plan.Count) throw new InvalidDataException("日志预览无效。");
        foreach (var file in plan)
        {
            var path = LiteralFile(directory, file.Name); var info = new FileInfo(path);
            if (Path.GetFileName(file.Name) != file.Name || !Allowed(info) || info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7) || info.Length != file.Bytes) throw new IOException("日志已变化，请重新扫描。");
            await using var stream = File.OpenRead(path); if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != file.Sha256) throw new IOException("日志已变化，请重新扫描。");
        }
        var target = Path.Combine(Path.GetFullPath(directory), ".shou-trash", Guid.NewGuid().ToString("N")); VerifiedDownloader.RejectLink(target); Directory.CreateDirectory(target);
        List<HistoricalLauncherLog> moved = [];
        try
        {
            foreach (var file in plan)
            {
                ct.ThrowIfCancellationRequested(); var source = LiteralFile(directory, file.Name);
                await using var read = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Delete, 16384, true);
                if (read.Length != file.Bytes || File.GetLastWriteTimeUtc(source) >= DateTime.UtcNow.AddDays(-7) || Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct)) != file.Sha256) throw new IOException("日志已变化，请重新扫描。");
                File.Move(source, LiteralFile(target, file.Name)); moved.Add(file);
            }
        }
        finally { await File.WriteAllTextAsync(Path.Combine(target, "receipt.json"), JsonSerializer.Serialize(moved), CancellationToken.None); }
        return target;
    }
    public static async Task RestoreAsync(string directory, string receiptDirectory, CancellationToken ct = default)
    {
        var root = Path.Combine(Path.GetFullPath(directory), ".shou-trash"); VerifiedDownloader.RejectLink(root);
        if (!Path.GetFullPath(receiptDirectory).StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("日志收据超出范围。");
        var receipt = Path.Combine(receiptDirectory, "receipt.json"); VerifiedDownloader.RejectLink(receipt);
        if (new FileInfo(receipt).Length > 65536) throw new InvalidDataException("日志收据过大。");
        var files = JsonSerializer.Deserialize<List<HistoricalLauncherLog>>(await File.ReadAllTextAsync(receipt, ct)) ?? [];
        if (files.Count > 100 || files.Any(f => f is null || string.IsNullOrEmpty(f.Sha256) || f.Sha256.Length != 64 || !Allowed(new FileInfo(LiteralFile(directory, f.Name))))) throw new InvalidDataException("日志收据无效。");
        foreach (var file in files)
        {
            if (Path.GetFileName(file.Name) != file.Name) throw new IOException("日志收据无效。");
            var from = LiteralFile(receiptDirectory, file.Name); var to = LiteralFile(directory, file.Name);
            if (File.Exists(to)) throw new IOException("存在同名日志，未覆盖。");
            await using var stream = File.OpenRead(from); if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != file.Sha256) throw new IOException("日志哈希已变化，未恢复。");
        }
        var remaining = files.ToList();
        try { foreach (var file in files) { ct.ThrowIfCancellationRequested(); File.Move(LiteralFile(receiptDirectory, file.Name), LiteralFile(directory, file.Name)); remaining.Remove(file); } }
        finally { VerifiedDownloader.RejectLink(receipt); await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(remaining), CancellationToken.None); }
    }
}
