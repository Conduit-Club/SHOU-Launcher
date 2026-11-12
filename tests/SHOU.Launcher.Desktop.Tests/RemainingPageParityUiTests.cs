using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class RemainingPageParityUiTests
{
    [AvaloniaFact]
    public async Task FavoritesMigrateGroupRoundTripAndShareExactlyTheWpfIdProtocol()
    {
        var path = Path.Combine(FixturePaths.Create("r47-favorites"), "favorites.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var first = Service.Project("cf-42") with { Slug = "fixture-cf" }; await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { first }));
        var service = new Service(); using var root = new MainWindowViewModel(service, new External());
        using (var vm = new DownloadsPageViewModel(root, service, path))
        {
            Assert.Single(vm.FavoriteFolders); Assert.Equal(new[] { "42" }, JsonSerializer.Deserialize<string[]>(vm.GetFavoriteShareCode()));
            vm.FavoriteFolderName = "Shaders"; await vm.CreateFavoriteFolderCommand.ExecuteAsync(null); Assert.Equal("Shaders", vm.SelectedFavoriteFolder!.Name); Assert.Empty(vm.FilteredFavoriteProjects);
            await vm.OpenProjectAsync(first); Assert.False(vm.IsProjectFavorite); await vm.ToggleFavoriteCommand.ExecuteAsync(null); Assert.Single(vm.FavoriteProjects); Assert.Single(vm.FilteredFavoriteProjects);
            vm.FavoriteShareCode = "[\"AANobbMI\",\"missing\"]"; await vm.ImportFavoriteShareCommand.ExecuteAsync(null); Assert.Equal(2, vm.FilteredFavoriteProjects.Count); Assert.Contains("missing", vm.FavoriteStatus);
            vm.FavoriteQuery = "Sodium"; Assert.Single(vm.FilteredFavoriteProjects); vm.SelectAllFavoritesCommand.Execute(null); Assert.Equal(new[] { "AANobbMI" }, JsonSerializer.Deserialize<string[]>(vm.GetFavoriteShareCode()));
            await vm.RemoveSelectedFavoritesCommand.ExecuteAsync(null); Assert.Single(vm.FavoriteProjects); Assert.Empty(vm.FilteredFavoriteProjects);
            vm.FavoriteQuery = ""; await vm.DeleteFavoriteFolderCommand.ExecuteAsync(null); Assert.Single(vm.FavoriteFolders); Assert.Single(vm.FilteredFavoriteProjects);
        }
        using var reloaded = new DownloadsPageViewModel(root, service, path); Assert.Single(reloaded.FavoriteFolders); Assert.Equal("cf-42", reloaded.FavoriteProjects.Single().Project.Id);
        var original = await File.ReadAllTextAsync(path); reloaded.FavoriteShareCode = "[\"../invalid\"]"; await reloaded.ImportFavoriteShareCommand.ExecuteAsync(null); Assert.Equal(original, await File.ReadAllTextAsync(path)); Assert.Single(reloaded.FavoriteProjects);
    }
    [AvaloniaFact]
    public async Task CustomHomeBuildsRealCardsImagesAndAWorkingWebButton()
    {
        var service = new Service(); var external = new External(); var store = new SettingsPresentationStore(service.Paths.DataDirectory);
        await store.SaveAsync(new(HomeSource: "local", HomeLocation: Path.Combine(service.Paths.DataDirectory, "Custom.xaml")));
        using var root = new MainWindowViewModel(service, external); var view = new HomeView { DataContext = root.Home }; var window = new Window { Width = 976, Height = 636, Content = view }; window.Show();
        try
        {
            await root.Home.RefreshCustomHomeCommand.ExecuteAsync(null);
            for (var n = 0; n < 100 && !view.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            var custom = view.GetVisualDescendants().OfType<CustomHomeView>().Single(); Assert.Contains(custom.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Fixture card");
            Assert.Contains(custom.GetVisualDescendants().OfType<Image>(), i => i.Source is not null);
            var button = custom.GetVisualDescendants().OfType<Button>().Single(); await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command!).ExecuteAsync(null); Assert.Equal("https://example.com/wiki", external.Opened.Single().AbsoluteUri);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task TranslationIgnoresLateResultsAndWikiEntryIsConditional()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new External()); var vm = root.Downloads; root.SelectedPage = vm; vm.SelectCategory("mod");
        await vm.OpenProjectAsync(Service.Project("AANobbMI")); Assert.True(vm.HasWikiReference); Assert.True(vm.CanTranslateDescription);
        service.TranslateGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var pending = vm.TranslateDescriptionCommand.ExecuteAsync(null);
        await vm.OpenProjectAsync(Service.Project("unknown") with { Slug = "unknown-fixture", Kind = "resourcepack" }); service.TranslateGate.SetResult("Old translation"); await pending; Assert.False(vm.HasTranslatedDescription); Assert.False(vm.HasWikiReference);
        service.TranslateGate = null; await vm.TranslateDescriptionCommand.ExecuteAsync(null); Assert.Equal("Translated fixture", vm.TranslatedDescription);
    }
    [AvaloniaFact]
    public async Task GlobalAdvancedSettingsPersistThroughTheExistingBackendOptions()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new External()); root.Settings.DebugLogging = true; root.Settings.ClasspathPrepend = Path.Combine(service.Paths.DataDirectory, "fixture.jar");
        await root.Settings.SaveCommand.ExecuteAsync(null); Assert.True(service.Snapshot.Settings.LaunchOptions!.DebugLogging); Assert.Equal(root.Settings.ClasspathPrepend, service.Snapshot.Settings.LaunchOptions.ClasspathPrepend);
    }
    private sealed class External : IExternalLauncher
    {
        public List<Uri> Opened = [];
        public Task OpenUriAsync(Uri uri, CancellationToken ct = default) { Opened.Add(uri); return Task.CompletedTask; }
        public Task OpenDirectoryAsync(string directory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Service : FakeLauncherService, ILauncherCatalogService, IContentDependencyCatalogService, IContentDescriptionService, ICustomHomeImageService, ILauncherInformationService
    {
        public TaskCompletionSource<string>? TranslateGate;
        public static ContentProject Project(string id) => new(id, "sodium", id == "AANobbMI" ? "Sodium" : "Fixture", "Description", "Author", 1, "mod", null, id.StartsWith("cf-") ? new("https://www.curseforge.com/minecraft/mc-mods/sodium") : new("https://modrinth.com/mod/sodium"), Source: id.StartsWith("cf-") ? "curseforge" : "modrinth");
        public Task<string?> GetChineseContentDescriptionAsync(ContentProject project, CancellationToken ct = default) => TranslateGate is null ? Task.FromResult<string?>("Translated fixture") : GetLate();
        private async Task<string?> GetLate() => await TranslateGate!.Task;
        public Task<ContentProject> GetContentProjectAsync(string id, CancellationToken ct = default) => id == "missing" ? throw new IOException("missing") : Task.FromResult(Project(id));
        public Task<ContentRelease> GetContentReleaseAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string version, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id, string? game = null, string? loader = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string url, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> ReadCustomHomeImageAsync(string source, string document, bool remote, CancellationToken ct = default) => Task.FromResult(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        public Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken ct = default) => Task.FromResult("<local:MyCard Title=\"Fixture card\"><StackPanel><TextBlock Text=\"Body\"/><Image Source=\"banner.png\"/><local:MyButton Text=\"Wiki\" EventType=\"打开网页\" EventData=\"https://example.com/wiki\"/></StackPanel></local:MyCard>");
        public Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherRelease>>([]);
        public Task<IReadOnlyList<LauncherFeedback>> GetLauncherFeedbackAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherFeedback>>([]);
    }
}
