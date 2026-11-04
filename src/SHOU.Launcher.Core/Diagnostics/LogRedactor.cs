using System.Text.RegularExpressions;

namespace SHOU.Launcher.Core.Diagnostics;

public sealed class LogRedactor
{
    private readonly HashSet<string> secrets = new(StringComparer.Ordinal);
    private readonly object sync = new();
    public void Register(string? secret) { if (!string.IsNullOrEmpty(secret)) lock (sync) secrets.Add(secret); }
    public string Scrub(string text)
    {
        lock (sync) foreach (var value in secrets.OrderByDescending(s => s.Length)) text = text.Replace(value, "<redacted>", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?i)(access[_-]?token|refresh[_-]?token|client[_-]?token|password|authorization|rpc[_-]?secret)(\s*[:=]\s*|\s+)([^\s,;]+)", "$1$2<redacted>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)Bearer\s+[^\s]+", "Bearer <redacted>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return text.Length > 16384 ? text[..16384] + "…[truncated]" : text;
    }
}
