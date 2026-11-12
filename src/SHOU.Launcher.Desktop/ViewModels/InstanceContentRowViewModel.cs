using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceContentRowViewModel : ObservableObject
{
    private InstanceContentEntry entry;
    private bool selected, duplicate;
    private InstanceModUpdate? update;
    public InstanceContentRowViewModel(InstanceContentEntry value, Action changed, Action<InstanceContentRowViewModel> requestUpdate, Action<InstanceContentRowViewModel> details)
    {
        entry = value;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Selected)) changed(); };
        UpdateCommand = new RelayCommand(() => requestUpdate(this)); DetailsCommand = new RelayCommand(() => details(this));
    }
    public InstanceContentEntry Entry { get => entry; internal set { SetProperty(ref entry, value); Refresh(); } }
    public string Name => Entry.Name;
    public bool IsMod => Entry.Kind == "mods";
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
    public bool Duplicate { get => duplicate; internal set { SetProperty(ref duplicate, value); Refresh(); } }
    public InstanceModUpdate? Update { get => update; internal set { SetProperty(ref update, value); Refresh(); } }
    public ContentProject? Project { get; internal set; }
    public bool HasUpdate => Update is not null;
    public bool HasError => Entry.MetadataError is not null;
    public string StateText => HasError ? Entry.MetadataError! : string.Join(" · ", new[] { Entry.Enabled ? "" : Localization.UiLocalization.Instance.Text("已禁用"), Duplicate ? Localization.UiLocalization.Instance.Text("重复项目") : "", Update is { } u ? Localization.UiLocalization.Instance.Format("可更新至 {0}", u.NewVersion) : "" }.Where(s => s.Length > 0));
    public string Description => Project?.Description ?? Entry.Description ?? "";
    public string Title => Project?.Title ?? Entry.DisplayName;
    public string Detail => string.Join("\n", new[] { Title, Entry.VersionLabel, Entry.Name, Description, Entry.ModId is { } id ? "ID: " + id : "", "必需依赖：" + string.Join(", ", Entry.Dependencies ?? []), "内嵌 JAR：" + string.Join(", ", Entry.EmbeddedMods ?? []), StateText }.Where(s => s.Length > 0));
    public IRelayCommand UpdateCommand { get; }
    public IRelayCommand DetailsCommand { get; }
    internal void Refresh() { foreach (var name in new[] { nameof(Title), nameof(Description), nameof(HasUpdate), nameof(HasError), nameof(StateText), nameof(Detail) }) OnPropertyChanged(name); }
}

public sealed class ModFilterViewModel(string key, Action<string> select) : ObservableObject
{
    private string title = key; private bool active;
    public string Key { get; } = key;
    public string Title { get => title; internal set => SetProperty(ref title, value); }
    public bool Active { get => active; internal set => SetProperty(ref active, value); }
    public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(key));
}
