using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    public ObservableCollection<PackManualFileRowViewModel> UpdateManualFiles { get; } = [];
    public bool HasUpdateManualFiles => UpdateManualFiles.Count > 0;
    private int manualUpdateGeneration;
    private void ClearManualUpdates()
    {
        manualUpdateGeneration++; UpdateManualFiles.Clear(); OnPropertyChanged(nameof(HasUpdateManualFiles));
    }
    private async Task<bool> PrepareManualUpdatesAsync(IReadOnlyList<InstanceModUpdate> updates)
    {
        ClearManualUpdates();
        if (Service is not IInstanceManualUpdateService service || updates.Count == 0) return true;
        var instance = SelectedInstance; var section = SelectedSection.Key; var generation = manualUpdateGeneration;
        bool Current() => !_workspaceDisposed && generation == manualUpdateGeneration && SelectedInstance == instance && SelectedSection.Key == section && ReferenceEquals(Owner.SelectedPage, this);
        var missing = new List<(InstanceModUpdate Update, CurseForgeManualFile File)>();
        if (!await Owner.RunInlineAsync("核对更新文件", async ct =>
        {
            foreach (var update in updates)
                foreach (var file in await service.GetInstanceUpdateManualFilesAsync(update.Token, ct)) missing.Add((update, file));
        }) || !Current()) return false;
        foreach (var group in missing.GroupBy(p => p.File))
        {
            var file = group.Key; var tokens = group.Select(p => p.Update.Token).Distinct().ToArray();
            UpdateManualFiles.Add(new(Owner, file, async (row, path) =>
            {
                if (!Current()) return;
                if (!await Owner.RunInlineAsync("校验官网文件", async ct =>
                {
                    // One verified cache entry can satisfy the same required dependency of several updates.
                    foreach (var token in tokens)
                        if ((await service.GetInstanceUpdateManualFilesAsync(token, ct)).Contains(file)) await service.ProvideInstanceUpdateManualFileAsync(token, file, path, ct);
                }) || !Current()) return;
                UpdateManualFiles.Remove(row); OnPropertyChanged(nameof(HasUpdateManualFiles));
                if (!HasUpdateManualFiles) await RequestContentUpdatesAsync(updates);
            }, row => Current() && UpdateManualFiles.Contains(row), () => Service is IInstanceManualUpdateService));
        }
        OnPropertyChanged(nameof(HasUpdateManualFiles));
        return missing.Count == 0;
    }
}
