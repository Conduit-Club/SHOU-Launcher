using System.Net;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IContentDescriptionService
{
    private readonly Dictionary<string, string> descriptionTranslations = [];
    public async Task<string?> GetChineseContentDescriptionAsync(ContentProject project, CancellationToken cancellationToken = default)
    {
        var id = project.Source == "curseforge" && project.Id.StartsWith("cf-", StringComparison.Ordinal) ? project.Id[3..] : project.Id;
        if (project.Source is not ("curseforge" or "modrinth") || id.Length is 0 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("资源 ID 无效。");
        var key = project.Source + "/" + id + "/" + project.Description;
        lock (descriptionTranslations) if (descriptionTranslations.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var json = JsonDocument.Parse(await ReadPublicDocumentAsync(new($"https://mod.mcimirror.top/translate/{project.Source}/{id}"), 262144, cancellationToken));
            if (!json.RootElement.TryGetProperty("translated", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return null;
            var text = value.GetString()!;
            lock (descriptionTranslations) { if (descriptionTranslations.Count >= 1000) descriptionTranslations.Clear(); descriptionTranslations[key] = text; }
            return text;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
}
