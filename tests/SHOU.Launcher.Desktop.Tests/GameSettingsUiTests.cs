using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Platform;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class GameSettingsUiTests
{
    [AvaloniaFact]
    public async Task GameControlsPersistDraftAndExposeAutomaticMemoryWithoutDefaultHelperParagraphs()
    {
        var service = new SettingsService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var settings = vm.Settings;
        settings.AutomaticMemory = true; Assert.Equal(9728, settings.EffectiveMemoryMiB); Assert.False(settings.CustomMemory);
        settings.Priority = GameProcessPriority.BelowNormal; settings.Visibility = LauncherVisibility.Hide; settings.IpPreference = GameIpPreference.IPv4;
        settings.GameTitle = "{name} / {version}"; settings.JvmArguments = "-XX:+UseG1GC"; settings.GameArguments = "--server example.invalid";
        settings.MaximumConnections = 3; settings.MaximumKiBPerSecond = 1024; settings.HttpsOnly = true; settings.UseCustomSources = false;
        await settings.SaveCommand.ExecuteAsync(null);
        var stored = service.Snapshot.Settings;
        Assert.True(stored.LaunchOptions!.AutomaticMemory); Assert.Equal(LauncherVisibility.Hide, stored.LaunchOptions.Visibility); Assert.Equal(GameIpPreference.IPv4, stored.LaunchOptions.IpPreference);
        Assert.Equal(3, stored.Downloads!.MaximumConnections); Assert.Equal(1024, stored.Downloads.MaximumKiBPerSecond); Assert.True(stored.Downloads.HttpsOnly);
        settings.GameTitle = "draft"; settings.DiscardDraftCommand.Execute(null); Assert.Equal("{name} / {version}", settings.GameTitle);
        settings.JvmArguments = "-javaagent:untrusted.jar"; await settings.SaveCommand.ExecuteAsync(null); Assert.Equal(1, service.SaveCalls); Assert.True(settings.IsDirty); Assert.Contains("JVM", vm.ErrorMessage);
    }
    [AvaloniaFact]
    public void All39LegacyIconsHaveDistinctBundledImagesAndExactNumericAliases()
    {
        Assert.Equal(39, LegacyAchievementIcons.Names.Count); var hashes = new HashSet<string>();
        for (var i = 0; i < 39; i++)
        {
            var name = LegacyAchievementIcons.Names[i]; Assert.Equal(name, LegacyAchievementIcons.Resolve((i + 1).ToString()));
            var png = ToolAchievementRenderer.Render(name, "Achievement get!", "Offline icon fixture", "");
            Assert.Equal(png, ToolAchievementRenderer.Render((i + 1).ToString(), "Achievement get!", "Offline icon fixture", ""));
            // The headless drawing backend produces placeholder pixels. Verify real bundled image bytes here;
            // native Skia output is checked separately by the Windows export acceptance.
            using var input = AssetLoader.Open(new Uri("avares://SHOU.Launcher.Desktop/Assets/AchievementIcons/" + name + ".png")); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            hashes.Add(Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        }
        Assert.Equal(39, hashes.Count); Assert.Null(LegacyAchievementIcons.Resolve("40")); Assert.Equal("diamond", LegacyAchievementIcons.Resolve("2"));
    }
    [AvaloniaFact]
    public void VisibilityPolicyHidesAndRestoresLauncherUsingSessionSignalsWithoutStartingAnyProcess()
    {
        var service = new SettingsService(); service.Publish(service.Snapshot with { Settings = new(LaunchOptions: new(Visibility: LauncherVisibility.Hide)) });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); var window = new MainWindow { DataContext = vm }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            service.Publish(service.Snapshot with { State = "Launching", GameSessions = [new("fixture", 987654, DateTimeOffset.UtcNow)] }); Dispatcher.UIThread.RunJobs(); Assert.True(window.IsVisible);
            service.Publish(service.Snapshot with { State = "Ready" }); Dispatcher.UIThread.RunJobs(); Assert.False(window.IsVisible);
            service.Publish(service.Snapshot with { State = "Ready", GameSessions = [] }); Dispatcher.UIThread.RunJobs(); Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task ExistingFolderPreviewRequiresSelectionAndInstanceOverridesSurviveRenameAndGlobalChanges()
    {
        var service = new SettingsService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var instances = vm.Instances; Assert.False(instances.AttachExistingCommand.CanExecute(null));
        await instances.InspectExistingFolderAsync("C:/fixture folder"); Assert.True(instances.HasExistingCandidates); Assert.True(instances.AttachExistingCommand.CanExecute(null));
        await instances.AttachExistingCommand.ExecuteAsync(null); Assert.Single(service.Snapshot.Instances); Assert.False(instances.HasExistingCandidates);
        instances.ShowInstanceSettingsCommand.Execute(null); instances.UseGlobalLaunchOptions = false;
        instances.InstanceIpPreference = GameIpPreference.IPv6; instances.InstanceWindowTitle = "Instance title"; instances.UseGlobalMemory = false; instances.InstanceMemoryMiB = 4096;
        instances.InstanceCustomWindow = true; instances.InstanceWindowWidth = 1280; instances.InstanceWindowHeight = 720; instances.InstanceFullscreen = true;
        await instances.SaveInstanceSettingsCommand.ExecuteAsync(null);
        Assert.Equal(GameIpPreference.IPv6, service.Snapshot.Instances.Single().LaunchOptions!.IpPreference); Assert.Equal(1280, service.Snapshot.Instances.Single().LaunchOptions!.WindowWidth);
        instances.BeginRenameCommand.Execute(null); instances.RenameDraft = "Renamed fixture"; await instances.SaveRenameCommand.ExecuteAsync(null);
        Assert.Equal(GameIpPreference.IPv6, service.Snapshot.Instances.Single().LaunchOptions!.IpPreference);
        vm.Settings.IpPreference = GameIpPreference.IPv4; await vm.Settings.SaveCommand.ExecuteAsync(null); Assert.Equal(GameIpPreference.IPv6, service.Snapshot.Instances.Single().LaunchOptions!.IpPreference);
        instances.UseGlobalLaunchOptions = true; await instances.SaveInstanceSettingsCommand.ExecuteAsync(null); Assert.Null(service.Snapshot.Instances.Single().LaunchOptions);
        Assert.Empty(service.Snapshot.GameSessions);
    }
    private sealed class SettingsService : FakeLauncherService, IGameSettingsService, IExistingInstanceService, IInstanceWorkspaceService
    {
        public MemoryStatus? ReadMemoryStatus() => new(32L << 30, 16L << 30);
        public Task<IReadOnlyList<ExistingInstanceCandidate>> InspectInstanceFolderAsync(string folder, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExistingInstanceCandidate>>([new("Existing", "local-fixture", "1.21.1", "fabric", "0.16.10", folder, new(folder, folder, "Existing", new string('a', 64)))]);
        public Task<GameInstance> AttachExistingInstanceAsync(ExistingInstanceCandidate candidate, CancellationToken cancellationToken = default)
        {
            var instance = new GameInstance("fixture", candidate.Name, candidate.VersionId, candidate.GameDirectory, Loader: candidate.Loader, GameVersionId: candidate.GameVersion, Origin: candidate.Origin);
            Publish(Snapshot with { Instances = [instance], SelectedInstanceId = instance.Id }); return Task.FromResult(instance);
        }
        public Task UpdateInstanceAsync(string instanceId, InstanceEdit edit, CancellationToken cancellationToken = default)
        { Publish(Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? i with { Name = edit.Name, MemoryMiB = edit.MemoryMiB, JavaExecutable = edit.JavaExecutable, LaunchOptions = edit.LaunchOptions } : i).ToArray() }); return Task.CompletedTask; }
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string instanceId, string kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstanceContentEntry>>([]);
        public Task ImportInstanceContentAsync(string instanceId, string kind, string sourcePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetInstanceModEnabledAsync(string instanceId, string relativePath, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExportInstanceAsync(string instanceId, bool includeWorlds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
