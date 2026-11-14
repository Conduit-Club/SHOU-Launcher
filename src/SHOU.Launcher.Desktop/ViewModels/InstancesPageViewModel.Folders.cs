using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record InstanceFolderChoice(string Title, string? Path)
{
    public bool HasPath => !string.IsNullOrEmpty(Path);
}
public sealed partial class InstancesPageViewModel
{
    public IInstanceArtworkService? ArtworkService => Service as IInstanceArtworkService;
    public ObservableCollection<InstanceFolderChoice> GameFolders { get; } = [];
    public ObservableCollection<string> CategoryFilters { get; } = ["全部分类"];
    private InstanceFolderChoice? selectedFolder;
    private string categoryFilter = "全部分类";
    public InstanceFolderChoice? SelectedGameFolder { get => selectedFolder; set { if (value is not null && SetProperty(ref selectedFolder, value)) FilterInstances(); } }
    public string InstanceCategoryFilter { get => categoryFilter; set { if (value is not null && SetProperty(ref categoryFilter, value)) FilterInstances(); } }
    private static string CategoryOf(GameInstance instance) => instance.Presentation?.Category is { } c && c != "auto" ? c : instance.Presentation?.Icon switch { "commandblock" => "预览版", "cobblestone" => "远古版", _ => instance.Loader is "vanilla" or "optifine" ? "原版" : "可安装模组" };
    private static bool SameFolder(string? a, string? b) => string.Equals(a is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), b is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private string FolderOf(GameInstance instance) => instance.Origin?.MinecraftRoot ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(instance.GameDirectory)) ?? Service.Paths.InstancesDirectory;
    private void RefreshFolders()
    {
        var paths = Instances.Select(FolderOf).Prepend(Service.Paths.InstancesDirectory).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        var rows = new[] { new InstanceFolderChoice("全部目录", null) }.Concat(paths.Select(p => new InstanceFolderChoice(SameFolder(p, Service.Paths.InstancesDirectory) ? "本地实例" : Path.GetFileName(Path.TrimEndingDirectorySeparator(p)), p))).ToArray();
        var old = selectedFolder?.Path;
        if (!GameFolders.SequenceEqual(rows)) { GameFolders.Clear(); foreach (var row in rows) GameFolders.Add(row); }
        SelectedGameFolder = GameFolders.FirstOrDefault(r => SameFolder(r.Path, old)) ?? GameFolders[0]; OnPropertyChanged(nameof(SelectedGameFolder));
        var categories = Instances.Select(CategoryOf).Distinct().Order().Prepend("全部分类").ToArray();
        if (!CategoryFilters.SequenceEqual(categories)) { CategoryFilters.Clear(); foreach (var c in categories) CategoryFilters.Add(c); }
        if (!CategoryFilters.Contains(InstanceCategoryFilter)) InstanceCategoryFilter = "全部分类";
    }
    public async Task<bool> SaveCroppedInstanceIconAsync(string instanceId, byte[] png)
    {
        if (SelectedInstance?.Id != instanceId || Service is not IInstanceArtworkService artwork || !CanEdit) return false;
        var success = await Owner.RunAsync("保存实例图标", ct => artwork.SaveInstanceIconAsync(instanceId, png, ct));
        if (success && SelectedInstance?.Id == instanceId)
        {
            _presentationRefreshing = true; try { PresentationIcon = InstanceIcons.Single(i => i.Id == "custom"); } finally { _presentationRefreshing = false; }
            Feedback = "实例图标已保存。"; OnPropertyChanged(nameof(InstanceIconKind));
        }
        return success;
    }
}
