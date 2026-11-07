using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Views;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ToolboxViewModel : PageViewModel, IDisposable
{
    private readonly IToolboxService? tools;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private TaskCompletionSource? idle;
    private string busyTool = "", actionFeedback = "", downloadFeedback = "", skinFeedback = "", serverFeedback = "", imageFeedback = "", avatarFeedback = "";
    private string downloadUrl = "", downloadDirectory, downloadName = "", playerName = "", serverAddress = "";
    private string itemId = "grass_block", achievementTitle = "Achievement get!", achievementLine1 = "Hello Minecraft", achievementLine2 = "";
    private GameLogCleanupPlan? cleanupPlan;
    private bool confirmCleanup;
    private bool cleanupRequested;
    private AccountTextureImage? avatarSkin;
    private byte[]? achievementPng, avatarPng, customItemIcon;
    private Bitmap? achievementPreview, avatarPreview;
    private int avatarSizeIndex;
    public ToolboxViewModel(MainWindowViewModel owner) : base(owner, "百宝箱", "启动器工具")
    {
        tools = Service as IToolboxService; downloadDirectory = Path.Combine(Service.Paths.DataDirectory, "Downloads");
        OpenCommunityUpdatesCommand = AsyncCommand(async () => { Owner.ShowHomeCommand.Execute(null); await Owner.Home.OpenReleasesCommand.ExecuteAsync(null); });
        FortuneCommand = AsyncCommand(() => RunAsync("actions", async ct => ActionFeedback = $"今日人品：{await tools!.GetDailyFortuneAsync(DateOnly.FromDateTime(DateTime.Now), ct)} / 100"), () => CanUseTools);
        LaunchCountCommand = Command(() =>
        {
            var instances = Service.Snapshot.Instances;
            ActionFeedback = $"已启动游戏 {instances.Sum(i => (decimal)(i.Presentation?.LaunchCount ?? 0)):0} 次。" +
                string.Concat(instances.Where(i => i.Presentation?.LaunchCount > 0).OrderByDescending(i => i.Presentation!.LaunchCount).Take(10).Select(i => $"\n{UiPrivacy.Redact(i.Name)}：{i.Presentation!.LaunchCount} 次"));
        }, () => !IsBusy && CanEdit);
        ScanCleanupCommand = AsyncCommand(() => RunAsync("actions", async ct =>
        {
            cleanupRequested = true; OnPropertyChanged(nameof(HasCleanupControls));
            cleanupPlan = await tools!.ScanGameLogsAsync(ct); CleanupFiles.Clear(); foreach (var file in cleanupPlan.Files) CleanupFiles.Add(file);
            ConfirmCleanup = false; ActionFeedback = cleanupPlan.Files.Count == 0 ? "没有超过一天的游戏日志或崩溃报告可整理。" : $"可整理 {cleanupPlan.Files.Count} 个文件，共 {cleanupPlan.Bytes / 1048576d:F2} MiB。";
            OnPropertyChanged(nameof(HasCleanupPreview));
        }), () => CanUseTools && !HasRunningGame);
        MoveCleanupCommand = AsyncCommand(() => RunAsync("actions", async ct =>
        {
            await tools!.MoveGameLogsAsync(cleanupPlan!, ct); ActionFeedback = "日志已移入实例内的回收目录，可恢复最近一次清理。";
            cleanupPlan = null; CleanupFiles.Clear(); ConfirmCleanup = false; OnPropertyChanged(nameof(HasCleanupPreview));
        }), () => CanUseTools && !HasRunningGame && ConfirmCleanup && HasCleanupPreview);
        RestoreCleanupCommand = AsyncCommand(() => RunAsync("actions", async ct =>
        { var count = await tools!.RestoreGameLogsAsync(ct); ActionFeedback = count == 0 ? "没有可恢复的日志。" : $"已恢复 {count} 个日志文件。"; }), () => CanUseTools && !HasRunningGame);
        DownloadCommand = AsyncCommand(() => RunAsync("download", async ct =>
        {
            var source = new Uri(DownloadUrl.Trim()); var name = DownloadName.Trim();
            if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..") throw new ArgumentException("文件名不能包含路径或目录。");
            var destination = Path.Combine(DownloadDirectory.Trim(), name);
            DownloadFeedback = "正在下载…";
            var progress = new DispatchProgress<TransferProgress>(p => Owner.Dispatch(() =>
            {
                if (ct.IsCancellationRequested || operation is null || operation.Token != ct) return;
                DownloadFeedback = p.TotalBytes is { } size ? $"正在下载：{p.BytesReceived / 1048576d:F1} / {size / 1048576d:F1} MiB" : $"正在下载：{p.BytesReceived / 1048576d:F1} MiB";
            }));
            await tools!.DownloadToolFileAsync(source, destination, progress, ct); DownloadFeedback = "已保存：" + DownloadName.Trim();
        }), () => CanUseTools && Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Path.IsPathFullyQualified(DownloadDirectory) && DownloadName.Trim().Length > 0);
        OpenDownloadDirectoryCommand = AsyncCommand(() => RunAsync("download", async ct =>
        { Directory.CreateDirectory(DownloadDirectory.Trim()); await Owner.ExternalLauncher.OpenDirectoryAsync(DownloadDirectory.Trim(), ct); }), () => !IsBusy && CanEdit && Path.IsPathFullyQualified(DownloadDirectory));
        QueryServerCommand = AsyncCommand(() => RunAsync("server", async ct =>
        { var server = await tools!.QueryToolServerAsync(ServerAddress, ct); ServerFeedback = server.OnlinePlayers is { } count ? $"在线 · {count} 人" : server.Status; }), () => CanUseTools && ServerAddress.Trim().Length > 0);
        PreviewAchievementCommand = AsyncCommand(() => RunAsync("image", ct =>
        { ct.ThrowIfCancellationRequested(); achievementPng = ToolAchievementRenderer.Render(ItemId, AchievementTitle, AchievementLine1, AchievementLine2, customItemIcon); ReplacePreview(ref achievementPreview, achievementPng); OnPropertyChanged(nameof(AchievementPreview)); OnPropertyChanged(nameof(HasAchievement)); ImageFeedback = ""; return Task.CompletedTask; }), () => !IsBusy && CanEdit);
        CancelCommand = Command(() => operation?.Cancel(), () => IsBusy);
    }
    public bool IsBusy => busyTool.Length != 0;
    public bool CanUseTools => !IsBusy && CanEdit && tools is not null;
    public bool IsActionsBusy => busyTool == "actions";
    public bool IsDownloadBusy => busyTool == "download";
    public bool IsSkinBusy => busyTool == "skin";
    public bool IsServerBusy => busyTool == "server";
    public bool IsImageBusy => busyTool is "image" or "avatar";
    public bool HasRunningGame => Service.Snapshot.GameSessions.Count > 0 || Service.Snapshot.GameProcessId is not null;
    public string ActionFeedback { get => actionFeedback; private set { SetProperty(ref actionFeedback, value); OnPropertyChanged(nameof(HasActionFeedback)); } }
    public bool HasActionFeedback => ActionFeedback.Length != 0;
    public string DownloadFeedback { get => downloadFeedback; private set { SetProperty(ref downloadFeedback, value); OnPropertyChanged(nameof(HasDownloadFeedback)); } }
    public bool HasDownloadFeedback => DownloadFeedback.Length != 0;
    public string SkinFeedback { get => skinFeedback; private set { SetProperty(ref skinFeedback, value); OnPropertyChanged(nameof(HasSkinFeedback)); } }
    public bool HasSkinFeedback => SkinFeedback.Length != 0;
    public string ServerFeedback { get => serverFeedback; private set { SetProperty(ref serverFeedback, value); OnPropertyChanged(nameof(HasServerFeedback)); } }
    public bool HasServerFeedback => ServerFeedback.Length != 0;
    public string ImageFeedback { get => imageFeedback; private set { SetProperty(ref imageFeedback, value); OnPropertyChanged(nameof(HasImageFeedback)); } }
    public bool HasImageFeedback => ImageFeedback.Length != 0;
    public string AvatarFeedback { get => avatarFeedback; private set { SetProperty(ref avatarFeedback, value); OnPropertyChanged(nameof(HasAvatarFeedback)); } }
    public bool HasAvatarFeedback => AvatarFeedback.Length != 0;
    public string DownloadUrl
    {
        get => downloadUrl;
        set
        {
            if (!SetProperty(ref downloadUrl, value ?? "")) return;
            if (DownloadName.Length == 0 && Uri.TryCreate(downloadUrl.Trim(), UriKind.Absolute, out var uri)) DownloadName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            NotifyCommands();
        }
    }
    public string DownloadDirectory { get => downloadDirectory; set { SetProperty(ref downloadDirectory, value ?? ""); NotifyCommands(); } }
    public string DownloadName { get => downloadName; set { SetProperty(ref downloadName, value ?? ""); NotifyCommands(); } }
    public string SkinPlayerName { get => playerName; set { SetProperty(ref playerName, value ?? ""); NotifyCommands(); } }
    public string ServerAddress { get => serverAddress; set { SetProperty(ref serverAddress, value ?? ""); NotifyCommands(); } }
    public ObservableCollection<GameLogFile> CleanupFiles { get; } = [];
    public bool HasCleanupPreview => cleanupPlan?.Files.Count > 0;
    public bool HasCleanupControls => cleanupRequested;
    public bool ConfirmCleanup { get => confirmCleanup; set { SetProperty(ref confirmCleanup, value); NotifyCommands(); } }
    public string ItemId { get => itemId; set { SetProperty(ref itemId, value ?? ""); InvalidateAchievement(); } }
    public string AchievementTitle { get => achievementTitle; set { SetProperty(ref achievementTitle, value ?? ""); InvalidateAchievement(); } }
    public string AchievementLine1 { get => achievementLine1; set { SetProperty(ref achievementLine1, value ?? ""); InvalidateAchievement(); } }
    public string AchievementLine2 { get => achievementLine2; set { SetProperty(ref achievementLine2, value ?? ""); InvalidateAchievement(); } }
    public Bitmap? AchievementPreview => achievementPreview;
    public bool HasAchievement => achievementPng is not null;
    public Bitmap? AvatarPreview => avatarPreview;
    public bool HasAvatar => avatarPng is not null;
    public int AvatarSizeIndex { get => avatarSizeIndex; set { if (SetProperty(ref avatarSizeIndex, Math.Clamp(value, 0, 2))) { BuildAvatar(); AvatarFeedback = ""; } } }
    public int AvatarSize => new[] { 64, 128, 256 }[AvatarSizeIndex];
    public AsyncRelayCommand FortuneCommand { get; }
    public AsyncRelayCommand OpenCommunityUpdatesCommand { get; }
    public RelayCommand LaunchCountCommand { get; }
    public AsyncRelayCommand ScanCleanupCommand { get; }
    public AsyncRelayCommand MoveCleanupCommand { get; }
    public AsyncRelayCommand RestoreCleanupCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand OpenDownloadDirectoryCommand { get; }
    public AsyncRelayCommand QueryServerCommand { get; }
    public AsyncRelayCommand PreviewAchievementCommand { get; }
    public RelayCommand CancelCommand { get; }
    public Task CreateShortcutAsync(string destination) => RunAsync("actions", async ct => { await LauncherShortcut.CreateAsync(destination, Service.Paths.DataDirectory, ct); ActionFeedback = "启动器快捷方式已创建。"; });
    public Task SaveSkinAsync(string destination) => RunAsync("skin", async ct =>
    { var image = await tools!.DownloadPublicSkinAsync(SkinPlayerName.Trim(), ct); await WriteImageAsync(destination, image.Png, ct); SkinFeedback = "皮肤已保存。"; });
    public Task LoadAvatarAsync(string path) => RunAsync("avatar", async ct =>
    { var bytes = await AccountAppearanceStore.ReadPickedSkinAsync(path, ct); avatarSkin = AccountAppearancePng.Decode(bytes); BuildAvatar(); AvatarFeedback = ""; });
    public Task LoadItemIconAsync(string path) => RunAsync("image", async ct =>
    {
        var info = new FileInfo(path); if (info.Length is < 45 or > 2 * 1024 * 1024) throw new InvalidDataException("请选择不超过 2 MiB 的 PNG 图标。");
        var bytes = await File.ReadAllBytesAsync(path, ct); ToolAchievementRenderer.ValidateIcon(bytes); customItemIcon = bytes; InvalidateAchievement(); ImageFeedback = "已选择物品图标。";
    });
    public Task SaveAchievementAsync(string destination) => RunAsync("image", async ct => { if (achievementPng is null) return; await WriteImageAsync(destination, achievementPng, ct); ImageFeedback = "成就图片已保存。"; });
    public Task SaveAvatarAsync(string destination) => RunAsync("avatar", async ct => { if (avatarPng is null) return; await WriteImageAsync(destination, avatarPng, ct); AvatarFeedback = "头像已保存。"; });
    private static async Task WriteImageAsync(string destination, ReadOnlyMemory<byte> png, CancellationToken ct)
    {
        destination = ToolFileTransfer.ValidateDestination(destination); var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".shou-image-" + Guid.NewGuid().ToString("N") + ".part"); var owned = false;
        try
        {
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true)) { owned = true; await stream.WriteAsync(png, ct); await stream.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested(); ToolFileTransfer.ValidateDestination(destination); File.Move(stage, destination, false); owned = false;
        }
        finally { if (owned && File.Exists(stage)) File.Delete(stage); }
    }
    private void InvalidateAchievement() { achievementPng = null; achievementPreview?.Dispose(); achievementPreview = null; OnPropertyChanged(nameof(AchievementPreview)); OnPropertyChanged(nameof(HasAchievement)); }
    private void BuildAvatar() { if (avatarSkin is null) return; avatarPng = ToolImages.Avatar(avatarSkin, AvatarSize); ReplacePreview(ref avatarPreview, avatarPng); OnPropertyChanged(nameof(AvatarPreview)); OnPropertyChanged(nameof(HasAvatar)); }
    private static void ReplacePreview(ref Bitmap? field, byte[] png) { var replacement = new Bitmap(new MemoryStream(png)); field?.Dispose(); field = replacement; }
    private async Task RunAsync(string tool, Func<CancellationToken, Task> action)
    {
        if (IsBusy || !CanEdit || lifetime.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); idle = completion;
        operation = cancellation; busyTool = tool; NotifyBusy();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Feedback(tool, cancellation.IsCancellationRequested ? "已取消，可重新操作。" : "操作超时，请重试。"); }
        catch (Exception ex) { Feedback(tool, UiPrivacy.Redact(ex is System.Net.Http.HttpRequestException ? "无法连接服务，请稍后重试。" : ex.Message)); }
        finally { operation = null; busyTool = ""; NotifyBusy(); idle = null; completion.TrySetResult(); }
    }
    private void Feedback(string tool, string value)
    { switch (tool) { case "download": DownloadFeedback = value; break; case "skin": SkinFeedback = value; break; case "server": ServerFeedback = value; break; case "image": ImageFeedback = value; break; case "avatar": AvatarFeedback = value; break; default: ActionFeedback = value; break; } }
    private void NotifyBusy()
    { foreach (var name in new[] { nameof(IsBusy), nameof(CanUseTools), nameof(IsActionsBusy), nameof(IsDownloadBusy), nameof(IsSkinBusy), nameof(IsServerBusy), nameof(IsImageBusy) }) OnPropertyChanged(name); NotifyCommands(); }
    public override void Refresh(LauncherSnapshot snapshot) { base.Refresh(snapshot); OnPropertyChanged(nameof(CanUseTools)); OnPropertyChanged(nameof(HasRunningGame)); }
    public override void NotifyCommands() { base.NotifyCommands(); OnPropertyChanged(nameof(CanUseTools)); }
    public void Cancel() => operation?.Cancel();
    public Task CancelAndWaitAsync() { var pending = idle?.Task ?? Task.CompletedTask; Cancel(); return pending; }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); achievementPreview?.Dispose(); avatarPreview?.Dispose(); }
}
