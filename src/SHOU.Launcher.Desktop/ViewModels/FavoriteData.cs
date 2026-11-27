using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record FavoriteFolder(string Id, string Name, string[] Projects, Dictionary<string, string>? Notes = null, string[]? Unresolved = null)
{ public override string ToString() => Name; }
public sealed record FavoriteDocument(int Version, ContentProject[] Projects, FavoriteFolder[] Folders);
public sealed record LegacyFavoriteFolder(string Id, string Name, string[] Projects, Dictionary<string, string> Notes);
public static class FavoriteData
{
    public static string CanonicalId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("收藏 ID 无效。");
        return id.All(char.IsAsciiDigit) ? "cf-" + id : id;
    }
    public static IReadOnlyList<LegacyFavoriteFolder> ReadLegacy(string text, string defaultName)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1048576) throw new InvalidDataException("收藏文件超过大小限制。");
        using var json = JsonDocument.Parse(text);
        JsonElement Find(JsonElement node, int depth)
        {
            if (depth > 12) throw new InvalidDataException("收藏配置层级过深。");
            if (node.ValueKind == JsonValueKind.String) { using var nested = JsonDocument.Parse(node.GetString()!); return Find(nested.RootElement, depth + 1); }
            if (node.ValueKind == JsonValueKind.Array) return node.Clone();
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject()) if (property.Name.Equals("CompFavorites", StringComparison.OrdinalIgnoreCase)) return Find(property.Value, depth + 1);
                foreach (var property in node.EnumerateObject()) if (property.Name.Equals("Game", StringComparison.OrdinalIgnoreCase)) return Find(property.Value, depth + 1);
            }
            throw new InvalidDataException("请选择旧 WPF 收藏夹 JSON 或包含 CompFavorites 的配置导出。");
        }
        var array = Find(json.RootElement, 0); var items = array.EnumerateArray().ToArray();
        if (items.All(e => e.ValueKind is JsonValueKind.String or JsonValueKind.Number)) return [new("", defaultName, items.Select(e => CanonicalId(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())).Distinct().ToArray(), [])];
        var result = new List<LegacyFavoriteFolder>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Favs", out var favs) || favs.ValueKind != JsonValueKind.Array) throw new InvalidDataException("旧收藏夹格式无效。");
            var ids = favs.EnumerateArray().Select(e => CanonicalId(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())).Distinct().ToArray();
            var notes = new Dictionary<string, string>();
            if (item.TryGetProperty("Notes", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
                foreach (var note in annotations.EnumerateObject()) { var id = CanonicalId(note.Name); if (ids.Contains(id) && note.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(note.Value.GetString())) notes[id] = note.Value.GetString()!; }
            var name = item.TryGetProperty("Name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
            var identity = item.TryGetProperty("Id", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : "";
            result.Add(new(identity ?? "", string.IsNullOrWhiteSpace(name) ? defaultName : name!, ids, notes));
        }
        return result;
    }
    public static double SearchScore(ContentProject project, string note, string query)
    {
        static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        query = Normalize(query.Trim()); if (query.Length == 0) return 1;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (terms.Length > 1) { var scores = terms.Select(t => SearchScore(project, note, t)).ToArray(); return scores.Any(s => s <= 0) ? 0 : scores.Average(); }
        var fields = new[] { (project.Title, 10d), (ContentReferenceIndex.Find(project)?.Name ?? "", 10d), (note, 9d), (project.Slug, 8d), (project.Author, 6d), (project.Description, 4d), (string.Join(' ', (project.Categories ?? []).Concat((project.Categories ?? []).Select(DownloadsPageViewModel.CategoryName))), 3d), (project.Kind + " " + project.Source + " " + (project.Kind switch { "mod" => "模组", "modpack" => "整合包", "shader" => "光影包", "resourcepack" => "资源包", "world" => "世界", "datapack" => "数据包", _ => "" }), 2d) };
        var score = fields.Max(f => Normalize(f.Item1).Contains(query, StringComparison.Ordinal) ? f.Item2 : 0);
        if (score > 0 || query.Length is < 4 or > 64 || query.Any(c => !char.IsAsciiLetterOrDigit(c))) return score;
        foreach (var (field, weight) in fields.Take(4))
            foreach (var word in System.Text.RegularExpressions.Regex.Matches(Normalize(field), "[a-z0-9]+").Select(m => m.Value).Where(w => Math.Abs(w.Length - query.Length) <= 2).Take(64))
                if (Distance(query, word) <= Math.Max(1, query.Length / 4)) score = Math.Max(score, weight / 3);
        return score;
    }
    private static int Distance(string a, string b)
    {
        var matrix = new int[a.Length + 1, b.Length + 1]; for (var i = 0; i <= a.Length; i++) matrix[i, 0] = i; for (var j = 0; j <= b.Length; j++) matrix[0, j] = j;
        for (var i = 1; i <= a.Length; i++) for (var j = 1; j <= b.Length; j++) { matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)); if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) matrix[i, j] = Math.Min(matrix[i, j], matrix[i - 2, j - 2] + 1); }
        return matrix[a.Length, b.Length];
    }
}
