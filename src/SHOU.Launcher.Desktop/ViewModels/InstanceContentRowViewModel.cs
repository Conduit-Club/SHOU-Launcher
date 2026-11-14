using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceContentRowViewModel : ObservableObject
{
    private InstanceContentEntry entry;
    private bool selected, duplicate;
    private InstanceModUpdate? update;
    public InstanceContentRowViewModel(InstanceContentEntry value, Action changed, Func<InstanceContentRowViewModel, Task> requestUpdate, Action<InstanceContentRowViewModel> details,
        Func<InstanceContentRowViewModel, Task>? reveal = null, Func<InstanceContentRowViewModel, Task>? toggle = null, Action<InstanceContentRowViewModel>? trash = null)
    {
        entry = value;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Selected)) changed(); };
        UpdateCommand = new AsyncRelayCommand(() => requestUpdate(this)); DetailsCommand = new RelayCommand(() => details(this));
        RevealCommand = new AsyncRelayCommand(() => reveal?.Invoke(this) ?? Task.CompletedTask);
        ToggleCommand = new AsyncRelayCommand(() => toggle?.Invoke(this) ?? Task.CompletedTask);
        TrashCommand = new RelayCommand(() => trash?.Invoke(this));
    }
    public InstanceContentEntry Entry { get => entry; internal set { SetProperty(ref entry, value); Refresh(); } }
    public string Name => Entry.Name;
    public bool IsMod => Entry.Kind == "mods";
    public bool Selected { get => selected; set { if (!Entry.Directory) SetProperty(ref selected, value); } }
    public bool IsDirectory => Entry.Directory;
    public bool IsFile => !Entry.Directory;
    public bool Duplicate { get => duplicate; internal set { SetProperty(ref duplicate, value); Refresh(); } }
    public InstanceModUpdate? Update { get => update; internal set { SetProperty(ref update, value); Refresh(); } }
    public ContentProject? Project { get; internal set; }
    public IReadOnlyList<ContentDependency> ResourceRequirements { get; internal set; } = [];
    public bool HasUpdate => Update is not null;
    public bool HasError => Entry.MetadataError is not null;
    public string StateText => HasError ? Localization.UiLocalization.Instance.Text(Entry.MetadataError!) : string.Join(" · ", new[] { Entry.Enabled ? "" : Localization.UiLocalization.Instance.Text("已禁用"), Duplicate ? Localization.UiLocalization.Instance.Text("重复项目") : "", Update is { } u ? Localization.UiLocalization.Instance.Format("可更新至 {0}", u.NewVersion) : "" }.Where(s => s.Length > 0));
    public string Description => Project?.Description ?? Entry.Description ?? "";
    public string Title => Project?.Title ?? Entry.DisplayName;
    public string Version => Entry.VersionLabel.Length > 0 ? Entry.VersionLabel : Update?.CurrentVersion ?? "";
    public bool HasVersion => Version.Length > 0;
    public double TitleMaximum => IsMod ? 300 : 450;
    public string SecondaryText => Entry.Directory ? Localization.UiLocalization.Instance.Text("双击或按 Enter 进入文件夹") : string.Join(" · ", new[] { Entry.Enabled ? "" : Localization.UiLocalization.Instance.Text("已禁用"), Duplicate ? Localization.UiLocalization.Instance.Text("重复项目") : "", Path.GetFileNameWithoutExtension(Name) + (HasError ? ": " + Localization.UiLocalization.Instance.Text(Entry.MetadataError!) : Description.Length > 0 ? ": " + string.Join(" ", Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : "") }.Where(s => s.Length > 0));
    public IReadOnlyList<string> Tags => (Project?.Categories ?? []).Select(DownloadsPageViewModel.CategoryName).Select(Localization.UiLocalization.Instance.Text).Distinct().Take(3).ToArray();
    public string SearchTags => string.Join(" ", (Project?.Categories ?? []).Concat(Tags));
    public string FallbackIcon => Entry.Directory ? "folder-open" : Entry.Kind switch { "resourcepacks" => "layers", "shaders" => "sparkles", "schematics" => "file-box", _ => "puzzle" };
    public string ToggleCaption => Localization.UiLocalization.Instance.Text(Entry.Enabled ? "禁用模组" : "启用模组");
    public string ToggleIcon => Entry.Enabled ? "circle-minus" : "circle-check";
    public string UpdateCaption => Localization.UiLocalization.Instance.Text(IsMod ? "更新模组" : "更新资源");
    public string Detail => string.Join("\n", new[] { Title, Entry.VersionLabel, Entry.Name, Description, Entry.ModId is { } id ? "ID: " + id : "", IsMod ? "必需依赖：" + string.Join(", ", Entry.Dependencies ?? []) : "", IsMod ? "内嵌 JAR：" + string.Join(", ", Entry.EmbeddedMods ?? []) : "",
        ResourceRequirements.Count == 0 ? "" : Localization.UiLocalization.Instance.Text("资源版本依赖：") + string.Join(", ", ResourceRequirements.Select(d => Localization.UiLocalization.Instance.Text(d.Type switch { "required" => "必需", "incompatible" => "不兼容", "optional" => "可选", "embedded" => "内嵌", _ => d.Type }) + ": " + (d.ProjectId ?? d.VersionId ?? d.FileName ?? "?") + (d.ProjectId is not null && d.VersionId is not null ? " @ " + d.VersionId : ""))), StateText }.Where(s => s.Length > 0));
    public IAsyncRelayCommand UpdateCommand { get; }
    public IRelayCommand DetailsCommand { get; }
    public IAsyncRelayCommand RevealCommand { get; }
    public IAsyncRelayCommand ToggleCommand { get; }
    public IRelayCommand TrashCommand { get; }
    internal void Refresh() { foreach (var name in new[] { nameof(Title), nameof(Description), nameof(HasUpdate), nameof(HasError), nameof(StateText), nameof(Detail), nameof(Version), nameof(HasVersion), nameof(SecondaryText), nameof(Tags), nameof(SearchTags), nameof(FallbackIcon), nameof(ToggleCaption), nameof(ToggleIcon) }) OnPropertyChanged(name); }
}

public sealed class ModFilterViewModel(string key, Action<string> select) : ObservableObject
{
    private string title = key; private bool active, shown = true;
    public string Key { get; } = key;
    public string Title { get => title; internal set => SetProperty(ref title, value); }
    public bool Active { get => active; internal set => SetProperty(ref active, value); }
    public bool Shown { get => shown; internal set => SetProperty(ref shown, value); }
    public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(key));
}
