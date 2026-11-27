using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ScreenshotClipboardRaceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public async Task OlderSuspendedReadCannotOverwriteTheSecondCopiedImage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clipboard = fixture.Window.Clipboard!;
        foreach (var (name, width) in new[] { ("first.png", 1), ("second.png", 2) })
        {
            var file = fixture.Service.Rows.Single(row => row.Name == name);
            var png = await File.ReadAllBytesAsync(file.FullPath);
            Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        }
        var gate = fixture.DelayNextList();
        var older = fixture.ViewModel.CopyScreenshotAsync(fixture.First, clipboard);
        IAsyncDataTransfer? copiedData = null;
        IAsyncDataTransfer? repeatedData = null;
        object? secondImage = null;
        try
        {
            try
            {
                await gate.Entered.Task.WaitAsync(Deadline);
                // The publication lock must not cover this older request's suspended I/O.
                await fixture.ViewModel.CopyScreenshotAsync(fixture.Second, clipboard).WaitAsync(Deadline);
                copiedData = await clipboard.TryGetDataAsync();
                Assert.NotNull(copiedData); Assert.True(copiedData.Contains(DataFormat.Bitmap));
                secondImage = await copiedData.TryGetBitmapAsync();
                Assert.NotNull(secondImage);
                // Headless drawing reports 1x1 for both decodes, but keeps the transferred object.
                repeatedData = await clipboard.TryGetDataAsync();
                Assert.NotNull(repeatedData);
                Assert.Same(secondImage, await repeatedData.TryGetBitmapAsync());
            }
            finally { gate.Release.TrySetResult(fixture.Service.Rows); await older.WaitAsync(Deadline); }
            using var finalData = await clipboard.TryGetDataAsync();
            Assert.NotNull(finalData); Assert.True(finalData.Contains(DataFormat.Bitmap));
            Assert.Same(secondImage, await finalData.TryGetBitmapAsync());
            Assert.Equal("图片已复制到剪贴板。", fixture.ViewModel.Feedback);
        }
        finally { repeatedData?.Dispose(); copiedData?.Dispose(); }
    }

    [AvaloniaFact]
    public async Task LeavingScreenshotPagePreventsLateClipboardAndFeedbackWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clipboard = fixture.Window.Clipboard!;
        var gate = fixture.DelayNextList();
        var pending = fixture.ViewModel.CopyScreenshotAsync(fixture.First, clipboard);
        string afterNavigation;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            await clipboard.ClearAsync();
            fixture.ViewModel.SelectedSection = fixture.ViewModel.Sections.Single(s => s.Key == "overview");
            afterNavigation = fixture.ViewModel.Feedback;
        }
        finally { gate.Release.TrySetResult(fixture.Service.Rows); await pending.WaitAsync(Deadline); }
        await AssertClipboardHasNoImageAsync(clipboard);
        Assert.Equal(afterNavigation, fixture.ViewModel.Feedback);
        Assert.False(fixture.ViewModel.IsScreenshots);
    }

    [AvaloniaFact]
    public async Task DisposingInstancesViewModelPreventsLateClipboardAndFeedbackWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clipboard = fixture.Window.Clipboard!;
        var gate = fixture.DelayNextList();
        var pending = fixture.ViewModel.CopyScreenshotAsync(fixture.First, clipboard);
        var beforeDispose = fixture.ViewModel.Feedback;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            await clipboard.ClearAsync();
            // Keep MainWindowVM alive so its lifetime cancellation cannot hide a VM-only bug.
            fixture.ViewModel.Dispose();
        }
        finally { gate.Release.TrySetResult(fixture.Service.Rows); await pending.WaitAsync(Deadline); }
        await AssertClipboardHasNoImageAsync(clipboard);
        Assert.Equal(beforeDispose, fixture.ViewModel.Feedback);
    }

    private static async Task AssertClipboardHasNoImageAsync(IClipboard clipboard)
    {
        using var data = await clipboard.TryGetDataAsync();
        Assert.True(data is null || !data.Contains(DataFormat.Bitmap));
    }

    private sealed record ReadGate(TaskCompletionSource Entered, TaskCompletionSource<IReadOnlyList<InstanceContentEntry>> Release);
    private sealed class Fixture(MainWindowViewModel owner, WorkspaceService service) : IAsyncDisposable
    {
        public WorkspaceService Service { get; } = service;
        public InstancesPageViewModel ViewModel => owner.Instances;
        public Window Window { get; } = new() { Width = 320, Height = 200 };
        public InstanceScreenshotPreview First => ViewModel.ScreenshotPreviews.Single(p => p.Name == "first.png");
        public InstanceScreenshotPreview Second => ViewModel.ScreenshotPreviews.Single(p => p.Name == "second.png");
        public static async Task<Fixture> CreateAsync()
        {
            var service = new WorkspaceService();
            await service.WritePngsAsync();
            var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
            var fixture = new Fixture(owner, service);
            try
            {
                fixture.Window.Show(); Dispatcher.UIThread.RunJobs();
                var clipboard = fixture.Window.Clipboard;
                Assert.NotNull(clipboard);
                await clipboard.ClearAsync();
                owner.Instances.ShowInstanceSettingsCommand.Execute(null);
                owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "screenshots");
                Dispatcher.UIThread.RunJobs();
                await (owner.Instances.RefreshContentCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Deadline);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, owner.Instances.ScreenshotPreviews.Count);
                Assert.All(owner.Instances.ScreenshotPreviews, image => Assert.True(image.HasPreview));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public ReadGate DelayNextList()
        {
            var gate = new ReadGate(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            var calls = 0;
            Service.Read = (_, kind, _) =>
            {
                Assert.Equal("screenshots", kind);
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(Service.Rows);
                gate.Entered.TrySetResult();
                // Deliberately ignore cancellation here: the consumer must reject a late reply.
                return gate.Release.Task;
            };
            return gate;
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Window.Clipboard is { } clipboard) await clipboard.ClearAsync(); }
            finally { Window.Close(); owner.Dispose(); }
        }
    }

    private static byte[] Png(int width)
    {
        using var raw = new MemoryStream(); raw.WriteByte(0);
        for (var x = 0; x < width; x++) raw.Write([30, 80, 140, 255]);
        using var png = new MemoryStream(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(raw.ToArray());
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return png.ToArray();
        void Chunk(string tag, byte[] bytes)
        {
            var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); png.Write(size);
            var data = Encoding.ASCII.GetBytes(tag).Concat(bytes).ToArray(); png.Write(data);
            var crc = uint.MaxValue;
            foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) * 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); png.Write(size);
        }
    }

    private sealed class WorkspaceService : ILauncherService, IInstanceWorkspaceService
    {
        private readonly FakeLauncherService _base = new();
        public IAppPaths Paths { get; } = new ClipboardPaths();
        public IReadOnlyList<InstanceContentEntry> Rows { get; private set; } = [];
        public Func<string, string, CancellationToken, Task<IReadOnlyList<InstanceContentEntry>>>? Read;
        public WorkspaceService()
        {
            var instance = _base.Snapshot.Instances[0] with { GameDirectory = Path.Combine(Paths.InstancesDirectory, "clipboard-fixture") };
            _base.Publish(_base.Snapshot with { Instances = [instance], SelectedInstanceId = instance.Id });
        }
        public async Task WritePngsAsync()
        {
            var directory = Path.Combine(Snapshot.Instances[0].GameDirectory, "screenshots"); Directory.CreateDirectory(directory);
            var rows = new List<InstanceContentEntry>();
            foreach (var (name, width) in new[] { ("first.png", 1), ("second.png", 2) })
            {
                var path = Path.Combine(directory, name); var bytes = Png(width); await File.WriteAllBytesAsync(path, bytes);
                rows.Add(new(name, "screenshots/" + name, path, "screenshots", bytes.Length, DateTimeOffset.UtcNow));
            }
            Rows = rows;
        }
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Read?.Invoke(id, kind, ct) ?? Task.FromResult(Rows);
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportInstanceContentAsync(string id, string kind, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => throw new NotSupportedException();
        public LauncherSnapshot Snapshot => _base.Snapshot;
        public IPlatformCapabilities Capabilities => _base.Capabilities;
        public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => _base.SnapshotChanged += value; remove => _base.SnapshotChanged -= value; }
        public event EventHandler<LauncherLog>? LogReceived { add => _base.LogReceived += value; remove => _base.LogReceived -= value; }
        public Task InitializeAsync(CancellationToken ct = default) => _base.InitializeAsync(ct);
        public Task<GameInstance> CreateInstanceAsync(string name, string version, CancellationToken ct = default) => _base.CreateInstanceAsync(name, version, ct);
        public Task<GameInstance> CreateFabricInstanceAsync(string name, string game, string loader, CancellationToken ct = default) => _base.CreateFabricInstanceAsync(name, game, loader, ct);
        public Task SelectInstanceAsync(string id, CancellationToken ct = default) => _base.SelectInstanceAsync(id, ct);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken ct = default) => _base.DiscoverJavaAsync(ct);
        public Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string java, CancellationToken ct = default) => _base.InspectJavaTrustAsync(java, ct);
        public Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string java, CancellationToken ct = default) => _base.CreateRepairedJavaCopyAsync(java, ct);
        public Task InstallInstanceAsync(string id, CancellationToken ct = default) => _base.InstallInstanceAsync(id, ct);
        public Task<LaunchPlan> BuildLaunchPlanAsync(string id, CancellationToken ct = default) => _base.BuildLaunchPlanAsync(id, ct);
        public Task LaunchAsync(string id, string server = "none", CancellationToken ct = default) => _base.LaunchAsync(id, server, ct);
        public Task StopGameAsync(CancellationToken ct = default) => _base.StopGameAsync(ct);
        public Task CancelDownloadAsync(string id, CancellationToken ct = default) => _base.CancelDownloadAsync(id, ct);
        public Task RetryDownloadAsync(string id, CancellationToken ct = default) => _base.RetryDownloadAsync(id, ct);
        public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default) => _base.SaveSettingsAsync(settings, ct);
        public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken ct = default) => _base.SaveConnectionSecretAsync(kind, secret, ct);
        public Task<AccountProfile> AddOfflineAccountAsync(string name, CancellationToken ct = default) => _base.AddOfflineAccountAsync(name, ct);
        public Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken ct = default) => _base.LoginMicrosoftAsync(challenge, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string name, string password, CancellationToken ct = default) => _base.LoginYggdrasilAsync(server, name, password, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string name, string password, Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> select, CancellationToken ct = default) => _base.LoginYggdrasilWithProfileSelectionAsync(server, name, password, select, ct);
        public Task RefreshAccountAsync(string id, CancellationToken ct = default) => _base.RefreshAccountAsync(id, ct);
        public Task SelectAccountAsync(string id, CancellationToken ct = default) => _base.SelectAccountAsync(id, ct);
        public Task RemoveAccountAsync(string id, CancellationToken ct = default) => _base.RemoveAccountAsync(id, ct);
        public Task RefreshServersAsync(CancellationToken ct = default) => _base.RefreshServersAsync(ct);
        public IReadOnlyList<LauncherLog> GetRecentLogs() => _base.GetRecentLogs();
        public ValueTask DisposeAsync() => _base.DisposeAsync();
    }

    private sealed class ClipboardPaths : IAppPaths
    {
        public string DataDirectory { get; } = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Guarded test root required."), "test-data/r5/screenshot-clipboard/" + Guid.NewGuid().ToString("N"));
        public ClipboardPaths() { foreach (var path in new[] { DataDirectory, LocalDataDirectory, TempDirectory }) Directory.CreateDirectory(path); }
        public string SharedDataDirectory => DataDirectory + "/shared";
        public string LocalDataDirectory => DataDirectory + "/local";
        public string CacheDirectory => DataDirectory + "/cache";
        public string LogsDirectory => DataDirectory + "/logs";
        public string RuntimesDirectory => DataDirectory + "/runtimes";
        public string InstancesDirectory => DataDirectory + "/instances";
        public string TempDirectory => DataDirectory + "/temp";
        public string ResourcesDirectory => DataDirectory + "/resources";
        public string LockFile => DataDirectory + "/instance.lock";
        public string CredentialScope => "clipboard-race-fixture";
        public bool IsIsolated => true;
    }
}
