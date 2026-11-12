using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Compatibility for local display metadata only; never used for remote identities or install manifests.</summary>
internal static class LocalDisplayJson
{
    internal static JsonNode? Parse(byte[] bytes) => Parse(Encoding.UTF8.GetString(bytes));
    internal static JsonNode? Parse(string text)
    {
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var output = new StringBuilder(text.Length); bool quoted = false, escaped = false, lineComment = false, blockComment = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i]; var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (lineComment) { output.Append(c); if (c is '\r' or '\n') lineComment = false; continue; }
            if (blockComment) { output.Append(c); if (c == '*' && next == '/') { output.Append(next); i++; blockComment = false; } continue; }
            if (quoted)
            {
                if (escaped) { output.Append(c); escaped = false; continue; }
                if (c == '\\') { output.Append(c); escaped = true; continue; }
                if (c == '"') quoted = false;
                else if (c < 0x20) { output.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); continue; }
            }
            else if (c == '"') quoted = true;
            else if (c == '/' && next is '/' or '*') { output.Append(c).Append(next); i++; lineComment = next == '/'; blockComment = next == '*'; continue; }
            output.Append(c);
        }
        return JsonNode.Parse(output.ToString(), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }
    internal static string? Description(string? text)
    {
        if (text is null) return null;
        var value = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length == 0 || value.Any(char.IsControl) ? null : value[..Math.Min(value.Length, 2048)];
    }
}
