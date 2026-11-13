using Avalonia.Threading;
using SHOU.Launcher.Core;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class HomePageViewModel
{
    private FileSystemWatcher? homeWatcher;
    private CancellationTokenSource? homeWatchDelay;
    private bool homeWatching;
    private string homeWatchPath = "", homeActionFeedback = "";
    public string HomeActionFeedback { get => homeActionFeedback; private set { SetProperty(ref homeActionFeedback, value); OnPropertyChanged(nameof(HasHomeActionFeedback)); } }
    public bool HasHomeActionFeedback => HomeActionFeedback.Length > 0;
    public void SetHomeFileWatching(bool active) { var changed = homeWatching != active; homeWatching = active; UpdateHomeFileWatcher(); if (active && changed && presentation.HomeSource == "local") _ = ReadCustomHomeAsync(); }
    private void UpdateHomeFileWatcher()
    {
        var target = homeWatching && presentation.HomeSource == "local" ? presentation.HomeLocation : "";
        if (homeWatcher is not null && target == homeWatchPath) return;
        homeWatcher?.Dispose(); homeWatcher = null; homeWatchDelay?.Cancel(); homeWatchDelay = null; homeWatchPath = target;
        if (target.Length == 0 || !Path.IsPathFullyQualified(target) || !Directory.Exists(Path.GetDirectoryName(target))) return;
        try
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(target)!, Path.GetFileName(target)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? sender, FileSystemEventArgs args) => Dispatcher.UIThread.Post(() =>
            { if (!ReferenceEquals(homeWatcher, watcher)) return; homeWatchDelay?.Cancel(); homeWatchDelay = new(); _ = ReadAfterChangeAsync(watcher, homeWatchDelay); });
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (s, e) => Changed(s, e); homeWatcher = watcher; watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { HomeActionFeedback = "主页文件监视失败：" + UiPrivacy.Redact(ex.Message); }
    }
    private async Task ReadAfterChangeAsync(FileSystemWatcher watcher, CancellationTokenSource delay)
    {
        try { await Task.Delay(250, delay.Token); if (ReferenceEquals(homeWatcher, watcher) && !delay.IsCancellationRequested) await ReadCustomHomeAsync(); }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(homeWatchDelay, delay)) homeWatchDelay = null; delay.Dispose(); }
    }
    public async Task ExecuteCustomHomeActionAsync(HomeAction action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (action.Type)
        {
            case "OpenUrl": await Owner.RunControlAsync("打开主页链接", token => Owner.ExternalLauncher.OpenUriAsync(new(action.Data), token)); break;
            case "RefreshHomepage": case "RefreshPage": await ReadCustomHomeAsync(); break;
            case "ShowHint": HomeActionFeedback = action.Data.Split('|')[0].Replace("\\n", "\n"); break;
            case "SwitchPage":
                var parts = action.Data.Split('|'); var section = parts[0].ToLowerInvariant(); var child = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                if (section is "launch" or "home" or "启动") Owner.SelectedPage = Owner.Home;
                else if (section is "download" or "downloads" or "下载")
                { var category = child switch { "downloadmod" => "mod", "downloadpack" => "modpack", "downloaddatapack" => "datapack", "downloadworld" => "world", "downloadinstall" => "minecraft", "downloadclient" => "installer-minecraft", "downloadresourcepack" => "resourcepack", "downloadshader" => "shader", "downloadcompfavorites" or "favorites" => "favorites", "" or "default" => "minecraft", _ => child }; Owner.Downloads.SelectCategory(category); Owner.SelectedPage = Owner.Downloads; }
                else if (section is "setup" or "settings" or "设置") { if (child.Length > 0 && child != "default") Owner.Settings.SelectedSection = child switch { "setupui" => "appearance", "setuplaunch" => "launch", "setupsystem" or "setuplaunchermisc" => "misc", "setupjava" => "java", "setupgamemanage" => "manage", "setupabout" => "about", "setuplog" => "logs", "setupfeedback" => "feedback", "setupupdate" => "updates", "setuplauncherlanguage" => "language", _ => child }; Owner.SelectedPage = Owner.Settings; }
                else if (section is "tools" or "other" or "工具") Owner.SelectedPage = Owner.Tools;
                else throw new InvalidDataException("尚未适配的主页页面入口：" + action.Data);
                break;
            default: throw new InvalidDataException("尚未适配的主页事件：" + action.Type);
        }
    }
}
