using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using PCL;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Diagnostics;

public static class CrashAnalysis
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Messages = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(typeof(CrashAnalysis).Assembly.GetManifestResourceStream("SHOU.crash-messages.json")!)!);
    public static async Task<CrashAnalysisReport> AnalyzeAsync(IReadOnlyList<string> files, string language = "zh-CN", IReadOnlyList<string>? modDirectories = null,
        CancellationToken ct = default, LogRedactor? redactor = null, bool automatic = false, string? instanceId = null)
    {
        redactor ??= new();
        var context = new CrashAnalysisContext(0, "\0"); var sources = new List<CrashLogSource>();
        async Task Read(Stream input, string name, string? path, DateTime modified)
        {
            ct.ThrowIfCancellationRequested(); using var reader = new StreamReader(input, leaveOpen: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) lines.Add(redactor.Scrub(line, truncate: false));
            context.RawFiles.Add(new(name, lines, modified));
            sources.Add(new(name, "log", string.Join('\n', lines), path));
        }
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(file);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true);
            if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
                foreach (var entry in zip.Entries.Where(e => IsLog(e.Name)))
                { await using var stream = entry.Open(); await Read(stream, entry.FullName.Replace('\\', '/'), null, entry.LastWriteTime.UtcDateTime); }
            }
            else if (Path.GetExtension(file).Equals(".bz2", StringComparison.OrdinalIgnoreCase))
            { using var bzip = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream(input) { IsStreamOwner = false }; await Read(bzip, Path.GetFileNameWithoutExtension(file), file, File.GetLastWriteTimeUtc(file)); }
            else if (Path.GetExtension(file).Equals(".gz", StringComparison.OrdinalIgnoreCase))
            { using var gzip = new GZipStream(input, CompressionMode.Decompress, true); await Read(gzip, Path.GetFileNameWithoutExtension(file), file, File.GetLastWriteTimeUtc(file)); }
            else await Read(input, automatic && file == files.LastOrDefault() ? Path.Combine(Path.GetDirectoryName(file)!, "rawoutput.log") : file, file, File.GetLastWriteTimeUtc(file));
        }
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            new CrashLogPreparer(context).Prepare(); ct.ThrowIfCancellationRequested();
            var logs = context.PreparedLogs!; var result = new CrashDetector().Analyze(logs, modDirectories); ct.ThrowIfCancellationRequested();
            var catalog = Messages.Value.GetValueOrDefault(language) ?? Messages.Value["zh-CN"];
            string Text(string key, object?[] args) => string.Format(CultureInfo.InvariantCulture, catalog.GetValueOrDefault(key) ?? Messages.Value["zh-CN"].GetValueOrDefault(key) ?? key, args);
            var message = new CrashResultFormatter(Text).Format(result, logs.All, !automatic);
            return new CrashAnalysisReport(message.Text, result.Findings.Select(f => new CrashDiagnosis(f.Cause.ToString(), f.Confidence.ToString(), f.Details.ToArray())).ToArray(),
                files.ToArray(), message.SuggestedAction == CrashSuggestedAction.OpenInstanceSettings, instanceId, sources);
        }, ct).ConfigureAwait(false);
    }
    private static bool IsLog(string name) => Path.GetExtension(name).ToLowerInvariant() is ".log" or ".txt";
    public static async Task ExportAsync(CrashAnalysisReport report, string destination, CancellationToken ct = default)
    {
        SafePath.RejectLinks(destination); var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                async Task Write(string name, string text)
                { await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); await using var writer = new StreamWriter(entry); await writer.WriteAsync(text.AsMemory(), ct); }
                await Write("分析结果.txt", report.Text); var index = 0;
                foreach (var log in report.Sources ?? [])
                    await Write($"logs/{++index:D3}-{Path.GetFileName(log.Name)}", log.Text);
            }
            ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(destination); File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
