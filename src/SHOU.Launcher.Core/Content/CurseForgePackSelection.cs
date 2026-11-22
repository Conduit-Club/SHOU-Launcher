using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Content;

internal static class CurseForgePackSelection
{
    internal static string Key(CurseForgePackReference file) => $"cf:{file.ProjectId}:{file.FileId}";
    internal static LocalPackOptionalFile Choice(CurseForgePackReference file, string? name = null, string? notice = null)
        => new(Key(file), name ?? $"CurseForge {file.ProjectId} / {file.FileId}", new($"https://www.curseforge.com/projects/{file.ProjectId}/files/{file.FileId}"), notice);
    internal static IReadOnlyList<CurseForgePackReference> Apply(IReadOnlyList<CurseForgePackReference> files, IReadOnlyList<string>? selected)
    {
        var keys = (selected ?? []).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != (selected?.Count ?? 0) || keys.Any(k => !files.Any(f => !f.Required && Key(f) == k)))
            throw new InvalidDataException("可选文件选择已失效或不属于此归档，请重新预检。");
        return files.Select(f => f with { Required = f.Required || keys.Contains(Key(f)) }).ToArray();
    }
    internal static string PlanKey(string sha, IReadOnlyList<string>? selected) => sha + ":" + string.Join(",", (selected ?? []).Order(StringComparer.Ordinal));
}
