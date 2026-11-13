using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private InstanceDirectoryMode directoryMode;
    private bool directoryConfirmed, initialMemory, consoleJava, debugLogging;
    private decimal initialMemoryMiB = 512;
    private string classpath = "", beforeExecutable = "", beforeArguments = "";
    public IReadOnlyList<InstanceDirectoryMode> DirectoryModes { get; } = Enum.GetValues<InstanceDirectoryMode>();
    public InstanceDirectoryMode DirectoryMode { get => directoryMode; set { if (SetProperty(ref directoryMode, value)) { _instanceDraftDirty = true; DirectoryChangeConfirmed = false; OnPropertyChanged(nameof(DirectoryPreview)); OnPropertyChanged(nameof(DirectoryWillChange)); } } }
    public bool DirectoryChangeConfirmed { get => directoryConfirmed; set => SetProperty(ref directoryConfirmed, value); }
    public string DirectoryPreview => SelectedInstance is { } i ? InstanceDirectoryPolicy.Resolve(i, InstanceDirectoryPolicy.Describe(i, Service.Paths) with { Mode = DirectoryMode }, Service.Snapshot.Settings) : "";
    public bool DirectoryWillChange => SelectedInstance is { } i && !string.Equals(Path.TrimEndingDirectorySeparator(DirectoryPreview), Path.TrimEndingDirectorySeparator(i.GameDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public bool InstanceInitialMemory { get => initialMemory; set { if (SetProperty(ref initialMemory, value)) _instanceDraftDirty = true; } }
    public decimal InstanceInitialMemoryMiB { get => initialMemoryMiB; set { if (SetProperty(ref initialMemoryMiB, value)) _instanceDraftDirty = true; } }
    public string InstanceClasspath { get => classpath; set { if (SetProperty(ref classpath, value ?? "")) _instanceDraftDirty = true; } }
    public string InstanceBeforeExecutable { get => beforeExecutable; set { if (SetProperty(ref beforeExecutable, value ?? "")) _instanceDraftDirty = true; } }
    public string InstanceBeforeArguments { get => beforeArguments; set { if (SetProperty(ref beforeArguments, value ?? "")) _instanceDraftDirty = true; } }
    public bool InstanceConsoleJava { get => consoleJava; set { if (SetProperty(ref consoleJava, value)) _instanceDraftDirty = true; } }
    public bool InstanceDebugLogging { get => debugLogging; set { if (SetProperty(ref debugLogging, value)) _instanceDraftDirty = true; } }
    public bool IsWindows => Service.Snapshot.Platform.OS == LauncherOS.Windows;
    public bool HasLegacySettings => SelectedInstance?.Origin is not null && Service is ILegacyInstanceSettingsService;
    private void LoadAdvanced(GameInstance i, GameLaunchOptions options)
    {
        DirectoryMode = i.Directories?.Mode ?? InstanceDirectoryMode.Current; DirectoryChangeConfirmed = false;
        InstanceInitialMemory = options.MinimumMemoryMiB.HasValue; InstanceInitialMemoryMiB = options.MinimumMemoryMiB ?? 512;
        InstanceClasspath = options.ClasspathPrepend; InstanceBeforeExecutable = options.BeforeLaunchExecutable; InstanceBeforeArguments = options.BeforeLaunchArguments;
        InstanceConsoleJava = options.UseConsoleJava; InstanceDebugLogging = options.DebugLogging;
        OnPropertyChanged(nameof(DirectoryPreview)); OnPropertyChanged(nameof(DirectoryWillChange));
        OnPropertyChanged(nameof(HasLegacySettings));
    }
    public async Task ReadLegacySettingsAsync()
    {
        if (SelectedInstance is not { } instance || Service is not ILegacyInstanceSettingsService legacy || !CanEdit) return;
        await Owner.RunAsync("读取旧版实例设置", async ct =>
        {
            var draft = await legacy.ReadLegacyInstanceSettingsAsync(instance.Id, ct);
            if (SelectedInstance?.Id != instance.Id) return;
            LoadInstanceLaunchOptions(instance with { LaunchOptions = draft.Options }, Service.Snapshot.Settings);
            if (draft.JavaExecutable is { } java) { InstanceJavaExecutable = java; UseGlobalJava = false; }
            if (draft.MemoryMiB is { } memory) { InstanceMemoryMiB = memory; UseGlobalMemory = false; }
            if (draft.DirectoryMode is { } mode) DirectoryMode = mode;
            _instanceDraftDirty = true;
            Feedback = draft.Warnings.Count == 0 ? "已读取为草稿，请核对后保存。" : string.Join("\n", draft.Warnings);
        });
    }
    public async Task ExportScriptAsync(string executable, string destination, bool overwrite)
    {
        if (SelectedInstance is not { } instance || Service is not ILaunchScriptService scripts || !CanEdit) return;
        if (_instanceDraftDirty) { Owner.ShowError("请先保存实例设置，再导出使用已保存设置的启动脚本。"); return; }
        if (await Owner.RunAsync("导出启动脚本", ct => scripts.ExportLaunchScriptAsync(instance.Id, executable, destination, overwrite, ct))) Feedback = "启动脚本已保存。";
    }
}
