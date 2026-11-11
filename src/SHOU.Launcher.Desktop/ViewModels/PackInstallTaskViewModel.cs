using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class PackTaskStepViewModel(string name, double weight) : ObservableObject
{
    public string Name { get; } = name;
    internal double Weight { get; } = weight;
    private string state = "等待中", detail = "";
    private double progress;
    private bool indeterminate;
    public string State => state;
    public string Detail => detail;
    public double Progress => progress;
    public bool IsIndeterminate => indeterminate;
    internal void Update(string status, double percent = 0, bool unknown = false, string text = "")
    {
        state = status; progress = Math.Clamp(percent, 0, 100); indeterminate = unknown; detail = text;
        foreach (var p in new[] { nameof(State), nameof(Progress), nameof(IsIndeterminate), nameof(Detail) }) OnPropertyChanged(p);
    }
}

public sealed class PackInstallTaskViewModel : ObservableObject
{
    public ObservableCollection<PackManualFileRowViewModel> ManualFiles { get; } = [];
    public bool HasManualFiles => ManualFiles.Count > 0;
    private string? manualArchiveSha;
    internal MainWindowViewModel Root => owner;
    private readonly MainWindowViewModel owner;
    private readonly DownloadsPageViewModel page;
    private readonly ILauncherCatalogService catalog;
    private readonly ILocalPackService? localPacks;
    private readonly IModrinthPackService? mrpacks;
    private readonly ContentFile file;
    private readonly string instanceName;
    private HashSet<string> priorTransfers = [];
    private string state = "等待中", detail = "", error = "";
    private string? cachedPath;
    private int stepIndex = -1, runVersion, completedFiles, totalFiles;
    private double speed;
    private GameInstance? importedInstance;
    internal string Identity => file.Sha1;
    public string Name { get; }
    public string State => state;
    public string Detail => detail;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public bool IsActive => state == "正在安装";
    public bool IsCompleted => state == "已完成";
    public int RemainingFiles => Math.Max(0, totalFiles - completedFiles);
    public string RemainingFileText => IsActive && totalFiles == 0 ? "统计中" : RemainingFiles.ToString();
    public double BytesPerSecond => speed;
    public double OverallProgress => Steps.Sum(s => s.Weight * s.Progress / 100);
    public ObservableCollection<PackTaskStepViewModel> Steps { get; } =
        [new("下载整合包文件", 30), new("解析安装清单", 10), new("安装整合包文件", 45), new("准备游戏本体与加载器", 15)];
    public AsyncRelayCommand RetryCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand OpenInstanceCommand { get; }
    internal PackInstallTaskViewModel(DownloadsPageViewModel page, MainWindowViewModel owner, ContentProject project, ContentRelease? release,
        ContentFile file, ILauncherCatalogService catalog, ILocalPackService? localPacks, IModrinthPackService? mrpacks)
    {
        this.page = page; this.owner = owner; this.file = file; this.catalog = catalog; this.localPacks = localPacks; this.mrpacks = mrpacks;
        var version = release?.Version;
        var display = release?.Name.Trim() ?? "";
        if (display.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || display.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) display = Path.GetFileNameWithoutExtension(display);
        static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var label = project.Source == "curseforge" && display.Length > 0 &&
            (Key(display).StartsWith(Key(project.Slug), StringComparison.Ordinal) || Key(display).StartsWith(Key(project.Title), StringComparison.Ordinal))
            ? display : project.Title + (string.IsNullOrWhiteSpace(version) ? "" : " " + version);
        instanceName = new string(label.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        if (instanceName.Length == 0) instanceName = "新整合包";
        Name = (project.Source == "curseforge" ? "CurseForge" : "Modrinth") + " 整合包下载：" + UiPrivacy.Redact(project.Title);
        RetryCommand = new(RunAsync, () => owner.CanMutate && state is "失败" or "已取消" or "需要补充文件");
        CancelCommand = new(() => { if (ReferenceEquals(page.ActivePackTask, this)) owner.CancelOperationCommand.Execute(null); }, () => IsActive);
        OpenInstanceCommand = new(async () =>
        {
            if (importedInstance is null) return;
            if (await owner.RunAsync("选择已安装实例", ct => owner.Service.SelectInstanceAsync(importedInstance.Id, ct))) owner.ShowHomeCommand.Execute(null);
        }, () => importedInstance is not null && owner.CanMutate && !IsActive);
    }
    internal async Task RunAsync()
    {
        if (!owner.CanMutate || page.ActivePackTask is not null) return;
        page.ActivePackTask = this; var generation = ++runVersion;
        priorTransfers = owner.Snapshot.Downloads.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        error = ""; state = "正在安装"; completedFiles = totalFiles = 0; speed = 0;
        ManualFiles.Clear(); manualArchiveSha = null;
        if (importedInstance is null) { stepIndex = -1; foreach (var step in Steps) step.Update("等待中"); }
        Changed(); owner.ShowTaskNotification(page, "已开始下载整合包", instanceName + " · 点击查看下载和安装进度");
        try
        {
            var success = await owner.RunBackgroundInstallAsync(Name, async ct =>
            {
                if (importedInstance is null)
                {
                    Activate(0); cachedPath = await catalog.DownloadContentAsync(file, ct); Steps[0].Update("已完成", 100);
                    Activate(1);
                    LocalPackInspection? inspected = null;
                    ModrinthPackInspection? mr = null;
                    var readProgress = ProgressFor(generation, 1);
                    if (localPacks is ILocalPackProgressService tracked) inspected = await tracked.InspectLocalPackWithProgressAsync(cachedPath, readProgress, ct);
                    else if (localPacks is not null) inspected = await localPacks.InspectLocalPackAsync(cachedPath, ct);
                    else if (mrpacks is not null) mr = await mrpacks.InspectModrinthPackAsync(cachedPath, ct);
                    else throw new NotSupportedException("当前后端未提供整合包安装能力。");
                    if (!(inspected?.CanImport ?? mr?.CanImport ?? false))
                    {
                        manualArchiveSha = inspected?.ArchiveSha256;
                        foreach (var missing in inspected?.ManualFiles ?? []) ManualFiles.Add(new(this, missing));
                        state = "需要补充文件";
                        error = (inspected?.CapabilityReason ?? mr?.CapabilityReason ?? "此整合包不能安装。") + Environment.NewLine +
                            string.Join(Environment.NewLine, inspected?.Warnings ?? mr?.Warnings ?? []);
                        throw new PackBlockedException();
                    }
                    Steps[1].Update("已完成", 100); Activate(2);
                    var progress = ProgressFor(generation, 2);
                    if (localPacks is not null)
                    {
                        var request = new LocalPackImportRequest(cachedPath, inspected!.ArchiveSha256, instanceName);
                        var installed = localPacks is ILocalPackProgressService reporting
                            ? await reporting.ImportLocalPackWithProgressAsync(request, progress, ct) : await localPacks.ImportLocalPackAsync(request, ct);
                        importedInstance = installed.Instance;
                    }
                    else importedInstance = (await mrpacks!.ImportModrinthPackAsync(new(cachedPath, mr!.ArchiveSha256, instanceName), ct)).Instance;
                    Steps[2].Update("已完成", 100);
                }
                Activate(3);
                if (owner.Service is IInstanceInstallProgressService preparing)
                    await preparing.InstallInstanceWithProgressAsync(importedInstance.Id, ProgressFor(generation, 3), ct);
                else await owner.Service.InstallInstanceAsync(importedInstance.Id, ct);
                Steps[3].Update("已完成", 100);
            }, ex =>
            {
                if (ex is not PackBlockedException) { state = "失败"; error = UiPrivacy.Redact(ex.Message); }
            });
            if (success) { state = "已完成"; detail = "已创建独立实例并准备好游戏文件；选择实例即可开始。"; completedFiles = totalFiles; }
            else if (state == "正在安装") { state = "已取消"; detail = importedInstance is null ? "已取消；可重试，未登记不完整实例。" : "已导入的实例已保留；重试将继续准备游戏文件。"; }
            if (!success && stepIndex >= 0) Steps[stepIndex].Update(state, Steps[stepIndex].Progress, text: error);
        }
        finally
        {
            if (ReferenceEquals(page.ActivePackTask, this)) page.ActivePackTask = null;
            speed = 0; Changed();
            owner.ShowTaskNotification(page, IsCompleted ? "整合包安装完成" : state, instanceName + " · 点击查看任务");
        }
    }
    private sealed class PackBlockedException : Exception;
    public async Task ProvideManualFileAsync(PackManualFileRowViewModel row, string path)
    {
        if (!owner.CanMutate || !ManualFiles.Contains(row) || cachedPath is null || manualArchiveSha is null || localPacks is not ILocalPackManualFileService manual) return;
        if (!await owner.RunAsync("校验补充文件", ct => manual.ProvideLocalPackFileAsync(cachedPath, manualArchiveSha, row.File.ProjectId, row.File.FileId, path, ct))) return;
        ManualFiles.Remove(row); Changed();
        if (ManualFiles.Count == 0) await RunAsync();
    }
    private IProgress<PackInstallProgress> ProgressFor(int generation, int step)
        => new Progress<PackInstallProgress>(p =>
        {
            if (generation != runVersion || !IsActive || stepIndex != step) return;
            completedFiles = Math.Max(0, p.CompletedFiles); totalFiles = Math.Max(completedFiles, p.TotalFiles);
            detail = UiPrivacy.Redact(p.Stage + (p.CurrentFile is null ? "" : "：" + p.CurrentFile));
            Steps[step].Update("进行中", step == 3 || totalFiles == 0 ? 0 : completedFiles * 100d / totalFiles, step == 3 || totalFiles == 0, detail + (totalFiles > 0 ? $" · {completedFiles}/{totalFiles} 项" : ""));
            Changed();
        });
    private void Activate(int step)
    {
        stepIndex = step; completedFiles = totalFiles = 0;
        priorTransfers = owner.Snapshot.Downloads.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        detail = Steps[step].Name; Steps[step].Update("进行中", unknown: true); Changed();
    }
    internal void Refresh(LauncherSnapshot snapshot)
    {
        if (IsActive)
        {
            var files = snapshot.Downloads.Where(t => !priorTransfers.Contains(t.Id)).ToArray();
            speed = snapshot.DownloadBytesPerSecond;
            var active = files.Where(t => t.State is "Queued" or "Downloading" or "Verifying" or "Retrying").ToArray();
            var transfer = active.OrderBy(t => t.UpdatedAt).LastOrDefault();
            if (transfer is not null && stepIndex is 0 or 2 or 3)
            {
                var transferDetail = $"{active.Length} 项传输/校验 · " + UiPrivacy.Redact(transfer.Name) + $" · {transfer.BytesReceived / 1048576d:0.0} / " +
                    (transfer.TotalBytes is { } size ? $"{size / 1048576d:0.0} MiB" : "未知大小");
                if (stepIndex == 0) detail = transfer.State == "Verifying" ? "整合包文件校验中" : transferDetail;
                if (stepIndex == 0) Steps[0].Update("进行中", transfer.TotalBytes is > 0 ? transfer.BytesReceived * 100d / transfer.TotalBytes.Value : 0,
                    transfer.State != "Downloading" || transfer.TotalBytes is null or <= 0, detail);
            }
        }
        Changed();
    }
    private void Changed()
    {
        foreach (var p in new[] { nameof(State), nameof(Detail), nameof(Error), nameof(HasError), nameof(HasManualFiles), nameof(IsActive), nameof(IsCompleted), nameof(RemainingFiles), nameof(RemainingFileText), nameof(BytesPerSecond), nameof(OverallProgress) }) OnPropertyChanged(p);
        RetryCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); OpenInstanceCommand.NotifyCanExecuteChanged(); page.NotifyPackTasks();
    }
}

public sealed class PackManualFileRowViewModel
{
    internal PackInstallTaskViewModel Task { get; }
    public CurseForgeManualFile File { get; }
    public string Name => File.FileName;
    public AsyncRelayCommand OpenPageCommand { get; }
    internal PackManualFileRowViewModel(PackInstallTaskViewModel task, CurseForgeManualFile file)
    {
        Task = task; File = file;
        OpenPageCommand = new(() => task.Root.RunControlAsync("打开文件官网", ct => task.Root.ExternalLauncher.OpenUriAsync(file.PageUri, ct)));
    }
}
