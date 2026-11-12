using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

// Local reference data extracted from the same mcmod.buf shipped by the WPF version.
public static class ContentReferenceIndex
{
    private sealed record Entry(int Id, string Name, string CurseForge, string Modrinth);
    public sealed record Reference(int Id, string Name) { public Uri PageUri => new($"https://www.mcmod.cn/class/{Id}.html"); }
    private static readonly Lazy<(Dictionary<string, Reference> Cf, Dictionary<string, Reference> Mr)> Index = new(() =>
    {
        using var stream = typeof(ContentReferenceIndex).Assembly.GetManifestResourceStream("SHOU.mcmod-index.json")!;
        var entries = JsonSerializer.Deserialize<Entry[]>(stream) ?? [];
        var cf = new Dictionary<string, Reference>(StringComparer.Ordinal); var mr = new Dictionary<string, Reference>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Id > 0))
        { var value = new Reference(entry.Id, entry.Name); if (entry.CurseForge.Length > 0) cf.TryAdd(entry.CurseForge, value); if (entry.Modrinth.Length > 0) mr.TryAdd(entry.Modrinth, value); }
        return (cf, mr);
    });
    public static Reference? Find(ContentProject project) => project.Kind is "mod" or "datapack" && (project.Source == "curseforge" ? Index.Value.Cf : Index.Value.Mr).TryGetValue(project.Slug, out var value) ? value : null;
}
