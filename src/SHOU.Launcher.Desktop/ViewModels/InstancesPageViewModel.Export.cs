using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private string _exportPackName = "", _exportPackVersion = "1.0.0";
    private string? _exportDraftInstanceId;
    private bool _exportPackConfig = true, _exportPackOptions, _exportPackLocalFiles;
    public string ExportPackName { get => _exportPackName; set => SetProperty(ref _exportPackName, value); }
    public string ExportPackVersion { get => _exportPackVersion; set => SetProperty(ref _exportPackVersion, value); }
    public bool ExportPackConfig { get => _exportPackConfig; set => SetProperty(ref _exportPackConfig, value); }
    public bool ExportPackOptions { get => _exportPackOptions; set => SetProperty(ref _exportPackOptions, value); }
    public bool ExportPackLocalFiles { get => _exportPackLocalFiles; set => SetProperty(ref _exportPackLocalFiles, value); }
    public AsyncRelayCommand ExportModrinthPackCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportCurseForgePackCommand { get; private set; } = null!;
    public RelayCommand OpenPackImportCommand { get; private set; } = null!;
    private void InitializePackExport()
    {
        InitializeExportSelection();
        ExportCurseForgePackCommand = AsyncCommand(async () =>
        {
            var instance = SelectedInstance!; var paths = new List<string>();
            void Add(string relative) { if (Path.Exists(Path.Combine(instance.GameDirectory, relative))) paths.Add(relative); }
            if (ExportPackConfig) { Add("config"); Add("defaultconfigs"); }
            if (ExportPackOptions) Add("options.txt");
            if (ExportPackLocalFiles) foreach (var path in new[] { "mods", "resourcepacks", "shaderpacks" }) Add(path);
            string? output = null;
            if (await Owner.RunAsync("导出 CurseForge 整合包", async ct => output = await ((ICurseForgeExportService)Service).ExportCurseForgePackAsync(instance.Id,
                new(ExportPackName.Trim(), ExportPackVersion.Trim(), MergeExportPaths(paths), ExportPackLocalFiles), ct))) Feedback = "已导出客户端整合包至 " + output;
        }, () => CanEdit && !IsSelectedInstanceRunning && SelectedInstance?.Loader is "vanilla" or "fabric" or "quilt" or "forge" or "neoforge" && Service is ICurseForgeExportService);
        OpenPackImportCommand = Command(() => { Owner.Downloads.SelectCategory("modpack"); Owner.SelectedPage = Owner.Downloads; }, () => CanEdit && Service is IModrinthPackService);
        ExportModrinthPackCommand = AsyncCommand(async () =>
    {
        var instance = SelectedInstance!; var selected = new List<string>();
        void AddExisting(string relative)
        {
            var path = Path.Combine(instance.GameDirectory, relative);
            if (File.Exists(path) || Directory.Exists(path)) selected.Add(relative);
        }
        if (ExportPackConfig) AddExisting("config");
        if (ExportPackOptions) AddExisting("options.txt");
        if (ExportPackLocalFiles) foreach (var path in new[] { "mods", "resourcepacks", "shaderpacks" }) AddExisting(path);
        var options = new ModrinthPackExportOptions(ExportPackName.Trim(), ExportPackVersion.Trim(), MergeExportPaths(selected), ExportPackLocalFiles);
        string? output = null;
        if (await Owner.RunAsync("导出Modrinth整合包", async ct => output = await ((IModrinthPackService)Service).ExportModrinthPackAsync(instance.Id, options, ct)))
            Feedback = "已导出客户端整合包至 " + output;
    }, () => CanEdit && !IsSelectedInstanceRunning && (SelectedInstance?.Loader is "vanilla" or "fabric" or "quilt" or "forge" or "neoforge") && Service is IModrinthPackService);
    }
    private void RefreshPackExport()
    {
        if (_exportDraftInstanceId == SelectedInstance?.Id) return;
        _exportDraftInstanceId = SelectedInstance?.Id; ExportPackName = SelectedInstance?.Name ?? ""; ExportPackVersion = "1.0.0";
    }
}
