using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class ToolboxViewModel
{
    private CacheCleanupPlan? cachePlan;
    private bool confirmCache;
    private string cacheFeedback = "";
    public string CacheFeedback { get => cacheFeedback; private set { SetProperty(ref cacheFeedback, value); OnPropertyChanged(nameof(HasCacheFeedback)); } }
    public bool HasCacheFeedback => CacheFeedback.Length != 0;
    public bool HasCachePreview => cachePlan?.Files.Count > 0;
    public bool ConfirmCache { get => confirmCache; set { SetProperty(ref confirmCache, value); NotifyCommands(); } }
    public ObservableCollection<CacheFile> CacheFiles { get; } = [];
    public AsyncRelayCommand ScanCacheCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearCacheCommand { get; private set; } = null!;
    private void InitializeCacheTools()
    {
        ScanCacheCommand = AsyncCommand(() => RunAsync("cache", async ct =>
        {
            cachePlan = null; CacheFiles.Clear(); ConfirmCache = false; OnPropertyChanged(nameof(HasCachePreview));
            var plan = await ((ICacheMaintenanceService)Service).ScanLauncherCacheAsync(ct);
            cachePlan = plan; foreach (var file in plan.Files.Take(200)) CacheFiles.Add(file);
            CacheFeedback = plan.Files.Count == 0 ? "没有可清理的缓存。" : $"可清理 {plan.Files.Count} 个文件，共 {plan.Bytes / 1048576d:F2} MiB。清理后会按需重新下载。";
            if (plan.SkippedFiles > 0) CacheFeedback += $" 已保留 {plan.SkippedFiles} 项导出、临时下载或不属于缓存的内容。";
            OnPropertyChanged(nameof(HasCachePreview));
        }), () => CanUseTools && !HasRunningGame && Service is ICacheMaintenanceService);
        ClearCacheCommand = AsyncCommand(() => RunAsync("cache", async ct =>
        {
            var result = await ((ICacheMaintenanceService)Service).ClearLauncherCacheAsync(cachePlan!, ct);
            CacheFeedback = $"已清理 {result.DeletedFiles} 个文件，释放 {result.FreedBytes / 1048576d:F2} MiB。" + (result.Cancelled ? "已取消剩余清理。" : "");
            if (result.SkippedFiles > 0) CacheFeedback += $"保留 {result.SkippedFiles} 项受保护或正在使用的内容。";
            cachePlan = null; CacheFiles.Clear(); ConfirmCache = false; OnPropertyChanged(nameof(HasCachePreview));
        }), () => CanUseTools && !HasRunningGame && ConfirmCache && HasCachePreview);
    }
}
