using CommunityToolkit.Mvvm.ComponentModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class FeatureHideChoice(SettingsPageViewModel owner, string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool Hidden { get => owner.IsFeatureHidden(Id); set { if (Hidden == value) return; owner.SetFeatureHidden(Id, value); } }
    internal void Refresh() => OnPropertyChanged(nameof(Hidden));
}
public sealed record FeatureHideGroup(string Title, IReadOnlyList<FeatureHideChoice> Choices);
public static class LauncherFeatures
{
    public static IReadOnlyList<(string Group, string Id, string Title)> Entries { get; } =
    [
        ("主页面", "download", "下载"), ("主页面", "settings", "设置"), ("主页面", "tools", "工具"),
        ("设置子页面", "settings-launch", "启动"), ("设置子页面", "settings-java", "Java"), ("设置子页面", "settings-manage", "管理"),
        ("设置子页面", "settings-appearance", "个性化"), ("设置子页面", "settings-language", "语言"), ("设置子页面", "settings-misc", "杂项"),
        ("设置子页面", "settings-updates", "软件更新"), ("设置子页面", "settings-about", "软件信息"), ("设置子页面", "settings-feedback", "反馈"), ("设置子页面", "settings-logs", "查看日志"),
        ("工具子页面", "tools-java", "Java 证书修复"), ("工具子页面", "tools-toolbox", "百宝箱"),
        ("实例设置子页面", "instance-modify", "修改"), ("实例设置子页面", "instance-export", "导出"), ("实例设置子页面", "instance-worlds", "存档"),
        ("实例设置子页面", "instance-screenshots", "截图"), ("实例设置子页面", "instance-mods", "模组"), ("实例设置子页面", "instance-resourcepacks", "资源包"),
        ("实例设置子页面", "instance-shaders", "光影包"), ("实例设置子页面", "instance-schematics", "投影原理图"), ("实例设置子页面", "instance-servers", "服务器"),
        ("实例设置子页面", "instance-relations", "模组关系"), ("特定功能", "instance-manager", "实例管理"), ("特定功能", "mod-updates", "模组更新"), ("特定功能", "feature-hide", "功能隐藏")
    ];
    public static bool IsKnown(string id) => Entries.Any(e => e.Id == id);
}
public sealed partial class MainWindowViewModel
{
    private bool revealHiddenFeatures;
    public bool RevealHiddenFeatures => revealHiddenFeatures;
    public bool IsFeatureVisible(string id) => revealHiddenFeatures || Settings?.SavedPresentation.HiddenSections?.Contains(id) != true;
    public IReadOnlyDictionary<string, bool> VisibleFeatures => LauncherFeatures.Entries.ToDictionary(e => e.Id, e => IsFeatureVisible(e.Id));
    public void ToggleHiddenFeatures()
    { revealHiddenFeatures = !revealHiddenFeatures; OnPropertyChanged(nameof(RevealHiddenFeatures)); NotifyLauncherAppearance(); }
    private void NotifyFeatureVisibility()
    {
        OnPropertyChanged(nameof(VisibleFeatures)); Instances?.NotifyFeatureNavigation();
    }
}
public sealed partial class SettingsPageViewModel
{
    public IReadOnlyList<FeatureHideGroup> FeatureGroups { get; private set; } = [];
    private void InitializeFeatureGroups() => FeatureGroups = LauncherFeatures.Entries.GroupBy(e => e.Group)
        .Select(g => new FeatureHideGroup(g.Key, g.Select(e => new FeatureHideChoice(this, e.Id, e.Title)).ToArray())).ToArray();
    internal bool IsFeatureHidden(string id) => appearanceOptions.HiddenSections?.Contains(id) == true;
    internal void SetFeatureHidden(string id, bool hidden)
    {
        if (!LauncherFeatures.IsKnown(id)) return;
        SetHidden(id, hidden); RefreshFeatureChoices();
    }
    private void RefreshFeatureChoices() { foreach (var choice in FeatureGroups.SelectMany(g => g.Choices)) choice.Refresh(); }
}
public sealed partial class InstancesPageViewModel
{
    public bool CanShowModUpdates => IsModsSection && Root.IsFeatureVisible("mod-updates") || IsResourceUpdateSection && Service is IInstanceResourceUpdateService;
    internal void NotifyFeatureNavigation() { OnPropertyChanged(nameof(GameSections)); OnPropertyChanged(nameof(ResourceSections)); OnPropertyChanged(nameof(HasModUpdateStatus)); OnPropertyChanged(nameof(CanShowModUpdates)); }
}
