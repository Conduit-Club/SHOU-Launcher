using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ResourceListPresentationTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 200 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static void Pump(Window w) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    private static MainWindowViewModel Open(Service service, string section, IExternalLauncher? external = null)
    {
        var root = new MainWindowViewModel(service, external ?? new FakeExternalLauncher()); var vm = root.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == section); return root;
    }
    [AvaloniaTheory]
    [InlineData("mods")]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task SharedCardsAndCompactTwoLineRowsKeepSixFilesVisibleAtMinimumWindow(string section)
    {
        using var root = Open(new Service(), section); var vm = root.Instances; var w = new MainWindow { Width = 880, Height = 560, DataContext = root }; w.Show();
        try
        {
            await Wait(() => vm.ContentEntries.Count == 10 && !vm.IsModScanBusy); Pump(w);
            var controls = w.GetVisualDescendants().OfType<Control>().ToArray(); var list = controls.OfType<ListBox>().Single(c => c.Name == "InstanceContentList");
            Assert.Contains(controls.OfType<Border>(), b => b.Name == "InstanceResourceToolbar" && b.IsVisible); Assert.Contains(controls.OfType<Border>(), b => b.Name == "InstanceResourceListCard" && b.IsVisible);
            var rows = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("resourceRow")).ToArray(); Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.InRange(row.Bounds.Height, 40, 48));
            Assert.True(rows.Count(row => row.TranslatePoint(new Point(0, row.Bounds.Height), w)!.Value.Y < w.ClientSize.Height) >= 6, string.Join("; ", rows.Select(row => $"top={row.TranslatePoint(default, w)!.Value.Y} height={row.Bounds.Height}")));
            Assert.Equal(section == "mods" ? 6 : 5, vm.ResourceToolbarColumns);
            var filter = controls.OfType<ToggleButton>().Single(c => c.Classes.Contains("resourceFilter") && c.IsChecked == true);
            var presenter = filter.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_ContentPresenter" && ReferenceEquals(c.TemplatedParent, filter));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(filter.Background).Color, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            var first = vm.FilteredContentRows[0]; Assert.Contains("Local description", first.SecondaryText); Assert.Equal(section switch { "resourcepacks" => "layers", "shaders" => "sparkles", _ => "puzzle" }, first.FallbackIcon);
            Assert.DoesNotContain(vm.ModFilters, f => f.Key == "禁用" && f.Shown); Assert.DoesNotContain(vm.ModFilters, f => f.Key == "启用" && f.Shown);
        }
        finally { w.Close(); }
    }
    [AvaloniaFact]
    public async Task PackAndShaderCategoryLabelsUseTheSelectedLanguageAndKeepRawSearchTags()
    {
        using var root = Open(new MetadataService { Categories = ["colored-lighting", "vanilla-like", "potato"] }, "resourcepacks");
        await Wait(() => root.Instances.FilteredContentRows.Any(r => r.Project is not null) && !root.Instances.IsModScanBusy);
        var row = root.Instances.FilteredContentRows.Single(r => r.Entry.RelativePath == "resourcepacks/file0.zip");
        try
        {
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                SHOU.Launcher.Desktop.Localization.UiLocalization.Instance.SetLanguage(language);
                Assert.Contains(language == "zh-CN" ? "彩色光照" : language == "zh-TW" ? "彩色光照" : "Colored lighting", row.Tags);
                Assert.Contains(language == "zh-CN" ? "原版风" : language == "zh-TW" ? "原版風" : "Vanilla-like", row.Tags);
                Assert.Contains("colored-lighting", row.SearchTags);
            }
        }
        finally { SHOU.Launcher.Desktop.Localization.UiLocalization.Instance.SetLanguage("zh-CN"); }
    }
    [AvaloniaFact]
    public async Task SearchIncludesOnlineTagsAndSelectedExportUsesTheActualSelection()
    {
        using var root = Open(new Service(), "mods"); var vm = root.Instances; await Wait(() => vm.ModUpdates.Count == 1 && !vm.IsModScanBusy);
        vm.ContentSearchText = "optimization"; Assert.Single(vm.FilteredContentRows); Assert.NotEmpty(vm.FilteredContentRows[0].Tags);
        vm.SelectAllModsCommand.Execute(null); var selectedName = vm.FilteredContentRows[0].Name; vm.ContentSearchText = "";
        var csv = vm.ExportContentInformation(true); Assert.Contains(selectedName, csv); Assert.Equal(2, csv.Split('\n').Length);
        vm.ClearModSelectionCommand.Execute(null); Assert.Equal(11, vm.ExportContentInformation(true).Split('\n').Length);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task PackSelectionAndExportDoNotPretendModUpdateOrEnableSupport(string section)
    {
        var service = new Service(); using var root = Open(service, section); var vm = root.Instances; await Wait(() => vm.ContentEntries.Count == 10);
        vm.SelectAllModsCommand.Execute(null); Assert.True(vm.HasModSelection); Assert.False(vm.HasSelectedMods); Assert.False(vm.CanManageMods); Assert.Equal(0, service.Scans);
        Assert.DoesNotContain(vm.ModFilters, f => f.Key == "可更新" && f.Shown); Assert.Equal(11, vm.ExportContentInformation(true).Split('\n').Length);
    }
    [AvaloniaFact]
    public async Task RowCommandsActOnTheirOwnFileAndRejectRowsFromPreviousPage()
    {
        var service = new Service(); var external = new External(); using var root = Open(service, "mods", external); var vm = root.Instances; await Wait(() => vm.ModUpdates.Count == 1 && !vm.IsModScanBusy);
        var row = vm.FilteredContentRows[0]; vm.FilteredContentRows[1].Selected = true;
        await row.RevealCommand.ExecuteAsync(null); Assert.Equal(row.Entry.FullPath, external.Revealed.Single());
        row.TrashCommand.Execute(null); Assert.Contains(row.Entry.RelativePath, vm.ModActionSummary); Assert.DoesNotContain(vm.FilteredContentRows[1].Entry.RelativePath, vm.ModActionSummary); vm.CancelModActionCommand.Execute(null);
        await row.ToggleCommand.ExecuteAsync(null); Assert.Equal(row.Entry.RelativePath, service.Toggled.Single());
        await Wait(() => !vm.IsModScanBusy); vm.SelectedSection = vm.Sections.Single(s => s.Key == "resourcepacks"); await row.RevealCommand.ExecuteAsync(null); Assert.Single(external.Revealed);
    }
    [AvaloniaFact]
    public async Task PointerSelectionAndInlineDetailsDoNotToggleTwiceOrClearTheSelection()
    {
        using var root = Open(new Service(), "mods"); var vm = root.Instances; var w = new MainWindow { Width = 976, Height = 636, DataContext = root }; w.Show();
        try
        {
            await Wait(() => vm.ContentEntries.Count == 10 && !vm.IsModScanBusy); Pump(w);
            var first = vm.FilteredContentRows[0]; var border = w.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("resourceRow"));
            var point = border.TranslatePoint(new Point(130, border.Bounds.Height / 2), w)!.Value; w.MouseMove(point); w.MouseDown(point, MouseButton.Left); w.MouseUp(point, MouseButton.Left); Pump(w); Assert.True(first.Selected);
            // Resolve the current geometry before clicking a row action.
            border = w.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("resourceRow")); var button = border.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContentRowDetailsButton");
            point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w)!.Value; w.MouseMove(point); w.MouseDown(point, MouseButton.Left); w.MouseUp(point, MouseButton.Left); Pump(w);
            Assert.True(first.Selected); Assert.True(vm.HasModDetail);
        }
        finally { w.Close(); }
    }
    [AvaloniaFact]
    public async Task ResourceMetadataEnrichesOriginalRowsWithoutAddingFakeUpdatesAndLatePageResultsAreIgnored()
    {
        var service = new MetadataService { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var root = Open(service, "resourcepacks"); var vm = root.Instances;
        await Wait(() => service.MetadataReads == 1); Assert.False(root.IsBusy); Assert.True(vm.IsModScanBusy);
        service.Gate.SetResult(); await Wait(() => !vm.IsModScanBusy); var enriched = vm.FilteredContentRows.Single(r => r.Entry.RelativePath == "resourcepacks/file0.zip"); Assert.Equal("Verified pack", enriched.Title); Assert.Equal("2.1", enriched.Version); Assert.False(enriched.HasUpdate);
        vm.ContentSearchText = "optimization"; Assert.Single(vm.FilteredContentRows);
        service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); vm.ContentSearchText = ""; await vm.RefreshContentCommand.ExecuteAsync(null); await Wait(() => service.MetadataReads == 2);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "shaders"); Assert.True(service.ReadToken.IsCancellationRequested); service.Gate.SetResult(); await Wait(() => vm.ContentEntries.Count == 10 && !vm.IsModScanBusy);
        Assert.All(vm.FilteredContentRows, row => Assert.Equal("shaders", row.Entry.Kind)); Assert.DoesNotContain(vm.FilteredContentRows, row => row.Title == "Verified pack");
    }
    private sealed class External : IExternalLauncher
    {
        public List<string> Revealed = [];
        public Task RevealFileAsync(string path, CancellationToken ct = default) { Revealed.Add(path); return Task.CompletedTask; }
        public Task OpenDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenUriAsync(Uri uri, CancellationToken ct = default) => Task.CompletedTask;
    }
    [AvaloniaFact]
    public void AnOlderServiceWithoutTrashSupportCannotOpenAnUnusableRowDeletePrompt()
    {
        using var root = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher()); var vm = root.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "mods");
        vm.ContentEntries.Add(new("one.jar", "mods/one.jar", "/fixture/one.jar", "mods", 1, DateTimeOffset.UtcNow));
        Assert.False(vm.CanTrashMods); vm.FilteredContentRows.Single().TrashCommand.Execute(null); Assert.False(vm.HasModAction);
    }
    private class Service : FakeLauncherService, IInstanceWorkspaceService, IInstanceModUpdateService, IInstanceModManagementService
    {
        public int Scans; public List<string> Toggled = [];
        public Service() { var i = Snapshot.Instances.Single() with { Loader = "fabric", GameVersionId = "1.21.1" }; Publish(Snapshot with { Instances = [i] }); }
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstanceContentEntry>>(Enumerable.Range(0, 10).Select(n => new InstanceContentEntry($"file{n}." + (kind == "mods" ? "jar" : "zip"), kind + $"/file{n}." + (kind == "mods" ? "jar" : "zip"), $"/fixture/{kind}/file{n}", kind, 10, DateTimeOffset.UtcNow, ModName: $"Resource {n}", ModVersion: "1.0", ModId: "id" + n, Description: "Local description")).ToArray());
        public Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string id, CancellationToken ct = default)
        { Scans++; return Task.FromResult(new InstanceModUpdateCheck([new("token", id, "mods/file0.jar", "Resource 0", "1.0", "2.0", 1)], 10, [], [new("mods/file0.jar", new("p", "p", "Resource 0", "Local description", "Author", 1, "mod", null, new Uri("https://modrinth.com/mod/p"), Categories: ["optimization", "utility"]))])); }
        public Task<InstanceModUpdateResult> ApplyInstanceModUpdateAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) { Toggled.Add(path); return Task.CompletedTask; }
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportInstanceContentAsync(string id, string kind, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("fixture.zip");
        public Task SetInstanceModsEnabledAsync(string id, IReadOnlyList<string> paths, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<InstanceModTrash> TrashInstanceModsAsync(string id, IReadOnlyList<string> paths, CancellationToken ct = default) => Task.FromResult(new InstanceModTrash("trash", paths.Count, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<InstanceModTrash>> GetInstanceModTrashAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceModTrash>>([]);
        public Task RestoreInstanceModsAsync(string id, string trash, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class MetadataService : Service, IInstanceContentMetadataService
    {
        public string[] Categories = ["optimization"];
        public TaskCompletionSource? Gate; public int MetadataReads; public CancellationToken ReadToken;
        public async Task<InstanceContentInfoCheck> ReadInstanceContentMetadataAsync(string id, string kind, CancellationToken ct = default)
        {
            MetadataReads++; ReadToken = ct; if (Gate is not null) await Gate.Task;
            return kind == "resourcepacks" ? new([new("resourcepacks/file0.zip", new("p", "p", "Verified pack", "Local description", "Author", 1, "resourcepack", null, new Uri("https://modrinth.com/resourcepack/p"), Categories: Categories), "2.1")], []) : new([], []);
        }
    }
}
