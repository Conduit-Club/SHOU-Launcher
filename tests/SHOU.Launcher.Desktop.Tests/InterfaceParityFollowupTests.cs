using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class InterfaceParityFollowupTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 300 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    [AvaloniaFact]
    public async Task ServerQueriesStayReadOnlySupportSingleRefreshAndIgnoreLatePageResults()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new External()); var vm = root.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "servers");
        await Wait(() => vm.InstanceServerRows.Count == 1 && service.QueryCalls == 1 && !vm.IsServerStatusBusy);
        var row = vm.InstanceServerRows.Single(); Assert.Equal("Fixture MOTD", row.Motd); Assert.Contains("2 / 16", row.StatusSummary); Assert.Contains("1.21.1", row.StatusSummary); Assert.False(vm.IsServerDraftDirty);
        await vm.RefreshSingleServerStatusCommand.ExecuteAsync(row); Assert.Equal(2, service.QueryCalls); Assert.Equal(0, service.ServerWrites);
        var window = new Window { Width = 976, Height = 636, Content = new InstanceWorldsView { DataContext = vm } }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            var copy = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyInstanceServerAddressButton"); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(row.Address, await window.Clipboard!.TryGetTextAsync()); await window.Clipboard!.ClearAsync();
            var target = copy.GetVisualAncestors().OfType<Control>().Single(g => g.ContextMenu is not null);
            var point = target.TranslatePoint(new Point(100, 20), window)!.Value; window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Dispatcher.UIThread.RunJobs();
            var menu = target.ContextMenu!; Assert.True(menu.IsOpen); var item = menu.Items.OfType<MenuItem>().Single(); Assert.Same(row, item.DataContext); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(row.Address, await window.Clipboard!.TryGetTextAsync()); menu.Close(); await window.Clipboard!.ClearAsync();
        }
        finally { window.Close(); }
        service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); var read = vm.RefreshInstanceServerStatusCommand.ExecuteAsync(null); await Wait(() => service.QueryCalls == 3);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "overview"); Assert.True(service.Token.IsCancellationRequested); service.Gate.SetResult(); await read; Assert.Equal("Fixture MOTD", row.Motd); Assert.Equal(0, service.ServerWrites);
    }
    [AvaloniaFact]
    public async Task HistoricalLogActionOpensItsActualFileAndFavoriteIconsLoadAndCanBeSearched()
    {
        var service = new Service(); var external = new External(); var logs = service.Paths.LogsDirectory; Directory.CreateDirectory(logs); var path = Path.Combine(logs, "history.log"); await File.WriteAllTextAsync(path, "fixture log");
        using var root = new MainWindowViewModel(service, external); root.Settings.SelectedSection = "logs";
        await root.Settings.LogFiles.Single(r => r.Name == "history.log").OpenCommand!.ExecuteAsync(null); Assert.Equal(path, external.Opened.Single());
        root.Downloads.SelectCategory("resourcepack"); root.SelectedPage = root.Downloads; await root.Downloads.OpenProjectAsync(Service.Project); await root.Downloads.ToggleFavoriteCommand.ExecuteAsync(null); root.Downloads.BackToListCommand.Execute(null); root.Downloads.SelectCategory("favorites");
        await Wait(() => root.Downloads.FavoriteProjects.Single().HasIcon); Assert.True(service.IconCalls >= 2);
        root.Downloads.FavoriteQuery = "Author"; Assert.Single(root.Downloads.FilteredFavoriteProjects); root.Downloads.FavoriteQuery = "missing"; Assert.Empty(root.Downloads.FilteredFavoriteProjects); Assert.Single(root.Downloads.FavoriteProjects); Assert.True(root.Downloads.HasNoFavoriteMatches);
        root.Downloads.FavoriteQuery = ""; Assert.False(root.Downloads.HasNoFavoriteMatches); Assert.Single(root.Downloads.FilteredFavoriteProjects);
    }
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConsoleJavaPreferenceIsOnlyExposedOnWindowsAndPreservesSavedValue(bool windows)
    {
        var service = new Service(); service.Publish(service.Snapshot with { Platform = new(windows ? LauncherOS.Windows : LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "fixture") });
        using var root = new MainWindowViewModel(service, new External()); Assert.Equal(windows, root.Settings.CanUseConsoleJava); root.Settings.UseConsoleJava = true; await root.Settings.SaveCommand.ExecuteAsync(null); Assert.True(service.Snapshot.Settings.LaunchOptions!.UseConsoleJava);
    }
    private sealed class External : IExternalLauncher, ILocalFileLauncher
    {
        public List<string> Opened = [];
        public Task OpenFileAsync(string file, CancellationToken ct = default) { Opened.Add(file); return Task.CompletedTask; }
        public Task OpenUriAsync(Uri uri, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Service : FakeLauncherService, IInstanceWorldService, IToolboxService, ILauncherCatalogService
    {
        public static ContentProject Project => new("fixture-icons", "fixture-icons", "Resource", "Description", "Author", 1, "resourcepack", "https://cdn.modrinth.com/fixture.png", new("https://modrinth.com/resourcepack/fixture-icons"));
        public TaskCompletionSource? Gate; public CancellationToken Token; public int QueryCalls, ServerWrites, IconCalls;
        public Task<InstanceServerList> GetInstanceServersAsync(string id, CancellationToken ct = default) => Task.FromResult(new InstanceServerList("revision", [new(0, "Fixture", "127.0.0.1:25565")]));
        public Task<InstanceServerList> SaveInstanceServersAsync(string id, string revision, IReadOnlyList<InstanceServerEdit> rows, CancellationToken ct = default) { ServerWrites++; throw new InvalidOperationException("A query must not save servers.dat"); }
        public Task<IReadOnlyList<InstanceWorldSummary>> GetInstanceWorldsAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceWorldSummary>>([]);
        public Task<IReadOnlyList<WorldDatapackFile>> GetWorldDatapacksAsync(string id, string world, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorldDatapackFile>>([]);
        public Task<WorldDatapackFile> ImportWorldDatapackAsync(string id, string world, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<ClubServer> QueryToolServerAsync(string address, CancellationToken cancellationToken = default) { QueryCalls++; Token = cancellationToken; if (Gate is not null) await Gate.Task; return new("fixture", "Fixture", address, "在线", 2, Gate is null ? "Fixture MOTD" : "Late response", 16, 15, "1.21.1"); }
        public Task DownloadToolFileAsync(Uri source, string destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountTextureImage> DownloadPublicSkinAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GameLogCleanupPlan> ScanGameLogsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveGameLogsAsync(GameLogCleanupPlan plan, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RestoreGameLogsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetDailyFortuneAsync(DateOnly date, CancellationToken ct = default) => Task.FromResult(1);
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string version, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id, string? game = null, string? loader = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string url, CancellationToken ct = default) { IconCalls++; return Task.FromResult(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==")); }
    }
}
