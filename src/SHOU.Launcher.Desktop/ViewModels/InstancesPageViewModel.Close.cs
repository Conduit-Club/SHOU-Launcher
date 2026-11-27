namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    public bool HasUnsavedInstanceChanges => _runtimeDirty || _instanceDraftDirty || IsDatapackOrderDirty || _worldDrafts.Values.Any(d => d.ServerDirty || d.EditorOpen)
        || worldSettingsDrafts.Values.Any(d => d.Dirty);
}
