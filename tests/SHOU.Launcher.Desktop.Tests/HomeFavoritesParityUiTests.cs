using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class HomeFavoritesParityUiTests
{
    [AvaloniaFact]
    public async Task GridPropertiesActionsDialogAndPageNavigationUseRealControls()
    {
        var service = new Service(); Directory.CreateDirectory(service.Paths.DataDirectory);
        var file = Path.Combine(service.Paths.DataDirectory, "Custom.xaml");
        await File.WriteAllTextAsync(file, """
            <?xml version="1.0"?><Grid Margin="4" ColumnDefinitions="2*,*" RowDefinitions="Auto,Auto">
              <TextBlock Name="Heading" Grid.Column="1" Text="Grid title" FontSize="23" FontWeight="Bold" Foreground="#ff0011"/>
              <local:MyButton Name="Copy" Grid.Row="1" Grid.ColumnSpan="2" Text="Copy"><local:MyButton.Events><local:CustomEvent Type="复制文本" Data="fixture clipboard"/><local:CustomEvent Type="弹出提示" Data="Copied|Finish"/></local:MyButton.Events></local:MyButton>
              <local:MyButton Name="Dialog" Text="Dialog" EventType="ShowDialog" EventData="Fixture dialog|Body|Close"/>
              <local:MyButton Name="Navigate" Text="Navigate" EventType="切换页面" EventData="Download|DownloadCompFavorites"/>
            </Grid>
            """);
        await new SettingsPresentationStore(service.Paths.DataDirectory).SaveAsync(new(HomeSource: "local", HomeLocation: file));
        using var root = new MainWindowViewModel(service, new External()); var view = new CustomHomeView { DataContext = root.Home }; var window = new Window { Content = view, Width = 976, Height = 636 }; window.Show();
        try
        {
            await root.Home.RefreshCustomHomeCommand.ExecuteAsync(null); window.UpdateLayout();
            var heading = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Heading"); Assert.Equal(1, Grid.GetColumn(heading)); Assert.Equal(FontWeight.Bold, heading.FontWeight); Assert.Equal(23, heading.FontSize);
            var grid = Assert.IsType<Grid>(heading.Parent); Assert.Equal(2, grid.ColumnDefinitions.Count); Assert.Equal(new Thickness(4), grid.Margin);
            Button Named(string name) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            Assert.Equal(2, Grid.GetColumnSpan(Named("Copy"))); await ((IAsyncRelayCommand)Named("Copy").Command!).ExecuteAsync(null); Assert.Equal("fixture clipboard", await window.Clipboard!.TryGetTextAsync()); Assert.Equal("Copied", root.Home.HomeActionFeedback);
            var dialog = ((IAsyncRelayCommand)Named("Dialog").Command!).ExecuteAsync(null); await Until(() => window.OwnedWindows.Any()); var owned = window.OwnedWindows.Single(); Assert.Equal("Fixture dialog", owned.Title); owned.Close(); await dialog;
            await ((IAsyncRelayCommand)Named("Navigate").Command!).ExecuteAsync(null); Assert.Same(root.Downloads, root.SelectedPage); Assert.True(root.Downloads.IsFavoritesPage); Assert.Null(service.LastLaunchServerId);
            await window.Clipboard!.ClearAsync();
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task LocalHomepageAtomicReplaceRefreshesAndDetachedWatcherStops()
    {
        var service = new Service(); Directory.CreateDirectory(service.Paths.DataDirectory); var file = Path.Combine(service.Paths.DataDirectory, "Custom.xaml"); await File.WriteAllTextAsync(file, "<TextBlock Text=\"First\"/>");
        await new SettingsPresentationStore(service.Paths.DataDirectory).SaveAsync(new(HomeSource: "local", HomeLocation: file)); using var root = new MainWindowViewModel(service, new External()); var view = new CustomHomeView { DataContext = root.Home }; var window = new Window { Content = view }; window.Show();
        try
        {
            await root.Home.RefreshCustomHomeCommand.ExecuteAsync(null); var next = file + ".new"; await File.WriteAllTextAsync(next, "<TextBlock Text=\"Second\"/>"); File.Move(next, file, true); await Until(() => root.Home.HomeDocument.Contains("Second"));
            window.Content = null; await File.WriteAllTextAsync(file, "<TextBlock Text=\"Third\"/>"); await Task.Delay(400); Dispatcher.UIThread.RunJobs(); Assert.Contains("Second", root.Home.HomeDocument);
            window.Content = view; await Until(() => root.Home.HomeDocument.Contains("Third"));
            await File.WriteAllTextAsync(file, "<Unsupported/>"); await Until(() => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text!.Contains("主页布局读取失败")));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task NotesCopyMoveAndDeleteConfirmationPreserveOtherFoldersAndBackup()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new External()); var path = Path.Combine(FixturePaths.Create("r48-notes"), "favorites.json"); using var vm = new DownloadsPageViewModel(root, service, path);
        await vm.OpenProjectAsync(Service.Project("AANobbMI")); await vm.ToggleFavoriteCommand.ExecuteAsync(null); var firstId = vm.SelectedFavoriteFolder!.Id;
        var row = vm.FilteredFavoriteProjects.Single(); row.EditFavoriteNoteCommand.Execute(null); row.FavoriteNoteDraft = "Never saved"; row.CancelFavoriteNoteCommand.Execute(null); Assert.Equal("", row.FavoriteNote);
        row.EditFavoriteNoteCommand.Execute(null); row.FavoriteNoteDraft = "Original note"; await row.SaveFavoriteNoteCommand.ExecuteAsync(null); Assert.Equal("Original note", vm.FilteredFavoriteProjects.Single().FavoriteNote);
        vm.FavoriteFolderName = "Other"; await vm.CreateFavoriteFolderCommand.ExecuteAsync(null); var otherId = vm.SelectedFavoriteFolder!.Id; vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Id == firstId); vm.FilteredFavoriteProjects.Single().IsSelected = true; await vm.CopySelectedFavoritesCommand.ExecuteAsync(null);
        vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Id == otherId); row = vm.FilteredFavoriteProjects.Single(); Assert.Equal("Original note", row.FavoriteNote); row.EditFavoriteNoteCommand.Execute(null); row.FavoriteNoteDraft = "Other note"; await row.SaveFavoriteNoteCommand.ExecuteAsync(null);
        vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Id == firstId); vm.FilteredFavoriteProjects.Single().IsSelected = true; await vm.MoveSelectedFavoritesCommand.ExecuteAsync(null); Assert.Empty(vm.FilteredFavoriteProjects);
        vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Id == otherId); Assert.Equal("Other note", vm.FilteredFavoriteProjects.Single().FavoriteNote); var before = await File.ReadAllTextAsync(path);
        await vm.DeleteFavoriteFolderCommand.ExecuteAsync(null); Assert.True(vm.HasFavoriteFolderDeletion); vm.CancelDeleteFavoriteFolderCommand.Execute(null); Assert.Equal(before, await File.ReadAllTextAsync(path));
        vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Id == firstId); await vm.DeleteFavoriteFolderCommand.ExecuteAsync(null); await vm.ConfirmDeleteFavoriteFolderCommand.ExecuteAsync(null); Assert.Single(vm.FavoriteFolders); Assert.Single(vm.FavoriteProjects); Assert.Equal(before, await File.ReadAllTextAsync(path + ".bak"));
        using var reload = new DownloadsPageViewModel(root, service, path); Assert.Equal("Other note", reload.FilteredFavoriteProjects.Single().FavoriteNote); Assert.False(reload.DeleteFavoriteFolderCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task WpfConfigurationImportRetainsUnresolvedNotesDeduplicatesAndSearchesTypos()
    {
        var service = new Service { Missing = true }; using var root = new MainWindowViewModel(service, new External()); var path = Path.Combine(FixturePaths.Create("r48-import"), "favorites.json");
        var legacy = JsonSerializer.Serialize(new { Game = new { CompFavorites = JsonSerializer.Serialize(new[] { new { Id = "legacy-id", Name = "WPF", Favs = new[] { "AANobbMI", "missing" }, Notes = new Dictionary<string, string> { ["missing"] = "Offline note", ["AANobbMI"] = "Performance" } } }) }, Credential = "never-import-this" });
        using (var vm = new DownloadsPageViewModel(root, service, path)) { await vm.ImportLegacyFavoritesAsync(legacy); await vm.ImportLegacyFavoritesAsync(legacy); Assert.Equal(2, vm.FavoriteFolders.Count); vm.SelectedFavoriteFolder = vm.FavoriteFolders.Single(f => f.Name == "WPF"); Assert.Single(vm.UnresolvedFavorites); Assert.Equal("Performance", vm.FilteredFavoriteProjects.Single().FavoriteNote); vm.FavoriteQuery = "soduim performance"; Assert.Single(vm.FilteredFavoriteProjects); Assert.Equal("搜索结果", vm.FavoriteGroups.Single().Title); }
        using var reloaded = new DownloadsPageViewModel(root, service, path); reloaded.SelectedFavoriteFolder = reloaded.FavoriteFolders.Single(f => f.Name == "WPF"); service.Missing = false; await reloaded.RetryUnresolvedFavoritesCommand.ExecuteAsync(null); Assert.Empty(reloaded.UnresolvedFavorites); Assert.Equal("Offline note", reloaded.FilteredFavoriteProjects.Single(p => p.Project.Id == "missing").FavoriteNote); Assert.DoesNotContain("never-import-this", await File.ReadAllTextAsync(path));
    }
    [AvaloniaFact]
    public async Task BatchDownloadsRequireCommonVersionIgnoreStalePickerAndSaveOnlyFiles()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new External()); var path = Path.Combine(FixturePaths.Create("r48-batch"), "favorites.json"); using var vm = new DownloadsPageViewModel(root, service, path);
        vm.FavoriteShareCode = "[\"AANobbMI\",\"translations\"]"; await vm.ImportFavoriteShareCommand.ExecuteAsync(null); vm.SelectAllFavoritesCommand.Execute(null); await vm.PrepareFavoriteDownloadCommand.ExecuteAsync(null); Assert.True(vm.HasFavoriteBatch); Assert.Equal(new[] { "1.21.1" }, vm.FavoriteBatchVersions); Assert.True(vm.CanSaveFavoriteBatch);
        var destination = Path.Combine(service.Paths.DataDirectory, "saved"); Directory.CreateDirectory(destination); var stale = vm.FavoriteBatchIdentity; vm.FilteredFavoriteProjects[0].IsSelected = false; await vm.SaveFavoriteDownloadAsync(destination, stale); Assert.Empty(Directory.GetFiles(destination));
        vm.SelectAllFavoritesCommand.Execute(null); await vm.PrepareFavoriteDownloadCommand.ExecuteAsync(null); await vm.SaveFavoriteDownloadAsync(destination, vm.FavoriteBatchIdentity); Assert.Equal(2, Directory.GetFiles(destination).Length); Assert.Contains("已保存 2", vm.FavoriteBatchStatus); Assert.True(root.IsTaskNotificationVisible); Assert.Null(service.LastLaunchServerId); Assert.Empty(service.Snapshot.Downloads);
        service.FailVersions = true; await vm.PrepareFavoriteDownloadCommand.ExecuteAsync(null); Assert.Contains("未下载", vm.FavoriteStatus); Assert.False(vm.CanSaveFavoriteBatch);
    }
    private static async Task Until(Func<bool> condition) { for (var i = 0; i < 500 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private sealed class External : IExternalLauncher
    {
        public Task OpenUriAsync(Uri uri, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenDirectoryAsync(string directory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Service : FakeLauncherService, ILauncherCatalogService, IContentDependencyCatalogService, ILauncherInformationService
    {
        public bool Missing, FailVersions;
        public static ContentProject Project(string id) => new(id, id == "AANobbMI" ? "sodium" : "translations", id == "AANobbMI" ? "Sodium" : "Translations", "Performance mod", "Author", 1, id == "AANobbMI" ? "mod" : "resourcepack", null, new("https://modrinth.com/mod/" + id));
        public Task<ContentProject> GetContentProjectAsync(string id, CancellationToken ct = default) => Missing && id == "missing" ? throw new IOException("offline fixture") : Task.FromResult(Project(id));
        public Task<ContentRelease> GetContentReleaseAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string version, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id, string? game = null, string? loader = null, CancellationToken cancellationToken = default) => FailVersions ? throw new IOException("offline fixture") : Task.FromResult<IReadOnlyList<ContentRelease>>([new(id, "Fixture", "1", DateTimeOffset.UtcNow, ["1.21.1"], id == "AANobbMI" ? ["fabric"] : [], [new(id + ".jar", new("https://cdn.modrinth.com/data/fixture/" + id), Convert.ToHexStringLower(SHA1.HashData(new byte[] { 1, 2 })), 2, true)], ProjectId: id)]);
        public async Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) { var path = Path.Combine(Paths.DataDirectory, "cache", file.FileName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [1, 2], ct); return path; }
        public Task<byte[]> GetContentIconAsync(string url, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken ct = default) => File.ReadAllTextAsync(location, ct);
        public Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherRelease>>([]);
        public Task<IReadOnlyList<LauncherFeedback>> GetLauncherFeedbackAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherFeedback>>([]);
    }
}
