using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

// Protocol fixtures only. No Java process, keytool or Minecraft is executed.
public sealed class JavaTrustSettingsTests
{
    private static string PathFor(string name) => Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "shou-ui-protocol-fixture", name, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
    private static string Source => PathFor("source");
    private static JavaTrustFinding Finding(bool repair = true, bool supported = true) => new("fixture/cacerts", true, new string('a', 64), repair, supported, "Explicit protocol fixture");
    private static JavaTrustDerivation Copy() => new(new(PathFor("derived"), Path.GetDirectoryName(Path.GetDirectoryName(PathFor("derived")))!, "21.0.13", 21, "fixture", RuntimeArchitecture.X64, "fixture:trust"), Source, "fixture-policy", new string('a', 64), new string('b', 64), Path.Combine(Path.GetDirectoryName(Source)!, "receipt.json"), [Finding(false)]);
    private static FakeLauncherService Fixture(string? selected = null, bool auto = false)
    {
        var fake = new FakeLauncherService();
        fake.Publish(fake.Snapshot with { Settings = fake.Snapshot.Settings with { JavaExecutable = auto ? null : selected ?? Source } });
        fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([Finding()]);
        fake.JavaTrustCopyBehavior = (_, _) => Task.FromResult(Copy()); return fake;
    }
    private static void Pump(Window w) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task Prepare(MainWindowViewModel root)
    { root.Tools.JavaTrust.TargetJava = Source; await root.Tools.JavaTrust.InspectCommand.ExecuteAsync(null); await root.Tools.JavaTrust.PrepareCopyCommand.ExecuteAsync(null); }

    [AvaloniaFact]
    public async Task ToolsOwnTheirTargetAndPreferenceWithoutSavingUnrelatedSettingsDrafts()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var w = new MainWindow { DataContext = root }; w.Show(); root.Settings.OpenJavaTrustToolsCommand.Execute(null); Pump(w);
        try
        {
            var tools = root.Tools.JavaTrust;
            Assert.Same(tools, w.GetVisualDescendants().OfType<JavaTrustToolsView>().Single().DataContext);
            root.Settings.Theme = "Dark"; tools.TargetJava = PathFor("other");
            Assert.Equal(Source, root.Settings.JavaExecutable); Assert.Equal(Source, fake.Snapshot.Settings.JavaExecutable);
            tools.AutoRepair = false; await tools.SavePreferenceCommand.ExecuteAsync(null);
            Assert.False(fake.Snapshot.Settings.ConduitJavaTrustAutoRepair); Assert.Equal("System", fake.Snapshot.Settings.Theme);
            Assert.Equal("Dark", root.Settings.Theme); Assert.True(root.Settings.IsDirty);
            root.Tools.ShowJavaSettingsCommand.Execute(null); Pump(w);
            Assert.DoesNotContain(w.GetVisualDescendants(), c => c is CheckBox { Name: "JavaTrustAutoRepairSetting" });
            Assert.True(Find<Button>(w, "OpenJavaTrustToolsButton").IsEffectivelyVisible);
            Assert.DoesNotContain("network", SettingsPageViewModel.SectionIds);
        }
        finally { w.Close(); }
    }
    [AvaloniaFact]
    public async Task PrepareDoesNotSelectAndExplicitSelectionRebasesOnlyTheUnchangedJavaDraft()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher()); var tools = root.Tools.JavaTrust;
        root.Settings.Theme = "Dark"; root.Settings.MemoryMiB = 3072;
        await Prepare(root); Assert.True(tools.HasPrepared); Assert.Equal(Source, fake.Snapshot.Settings.JavaExecutable); Assert.Equal(0, fake.SaveCalls);
        await tools.SelectCopyCommand.ExecuteAsync(null);
        Assert.Equal(Copy().Java.ExecutablePath, fake.Snapshot.Settings.JavaExecutable);
        Assert.Equal(Copy().Java.ExecutablePath, root.Settings.JavaExecutable); Assert.Equal("Dark", root.Settings.Theme);
        Assert.True(root.Settings.IsDirty); Assert.False(root.Settings.HasJavaSelectionConflict);
        await root.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Copy().Java.ExecutablePath, fake.Snapshot.Settings.JavaExecutable); Assert.Equal("Dark", fake.Snapshot.Settings.Theme);
        Assert.Equal(3072, fake.Snapshot.Settings.MemoryMiB); Assert.True(tools.HasRollback);
    }
    [AvaloniaFact]
    public async Task ExplicitConflictingJavaDraftNeedsAChoiceBeforeItCanOverwriteTheSavedSelection()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher());
        root.Settings.JavaExecutable = PathFor("draft"); await Prepare(root); await root.Tools.JavaTrust.SelectCopyCommand.ExecuteAsync(null);
        Assert.Equal(PathFor("draft"), root.Settings.JavaExecutable); Assert.True(root.Settings.HasJavaSelectionConflict); Assert.False(root.Settings.SaveCommand.CanExecute(null));
        root.Settings.KeepJavaDraftCommand.Execute(null); Assert.True(root.Settings.SaveCommand.CanExecute(null)); await root.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(PathFor("draft"), fake.Snapshot.Settings.JavaExecutable); Assert.False(root.Tools.JavaTrust.HasRollback);
    }
    [AvaloniaFact]
    public async Task UseSavedJavaChoiceRetainsOtherDraftsAndDoesNotWriteOnItsOwn()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher());
        root.Settings.Theme = "Dark"; root.Settings.JavaExecutable = PathFor("draft"); await Prepare(root); await root.Tools.JavaTrust.SelectCopyCommand.ExecuteAsync(null);
        root.Settings.UseSavedJavaSelectionCommand.Execute(null);
        Assert.Equal(Copy().Java.ExecutablePath, root.Settings.JavaExecutable); Assert.Equal("Dark", root.Settings.Theme);
        Assert.False(root.Settings.HasJavaSelectionConflict); Assert.True(root.Settings.IsDirty); Assert.Equal(0, fake.SaveCalls);
    }
    [AvaloniaFact]
    public async Task HealthyUnknownAndLateScanResultsCannotEnableAnUnjustifiedRepair()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher()); var tools = root.Tools.JavaTrust;
        tools.TargetJava = Source; Assert.False(tools.PrepareCopyCommand.CanExecute(null));
        fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([Finding(false)]);
        await tools.InspectCommand.ExecuteAsync(null); Assert.Contains("健康", tools.Status); Assert.False(tools.CanPrepare);
        fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([Finding(false, false)]);
        await tools.InspectCommand.ExecuteAsync(null); Assert.Contains("未知", tools.Status); Assert.False(tools.CanPrepare);
        var response = new TaskCompletionSource<IReadOnlyList<JavaTrustFinding>>(); fake.JavaTrustInspectBehavior = (_, _) => response.Task;
        var pending = tools.InspectCommand.ExecuteAsync(null); tools.TargetJava = PathFor("changed"); response.SetResult([Finding()]); await pending;
        Assert.Empty(tools.Findings); Assert.False(tools.CanPrepare); Assert.Equal(Source, fake.Snapshot.Settings.JavaExecutable);
    }
    [AvaloniaFact]
    public async Task CancelledPreparationAndFailedSelectionNeverChooseAnIncompleteCopy()
    {
        var fake = Fixture(); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher()); var tools = root.Tools.JavaTrust;
        tools.TargetJava = Source; await tools.InspectCommand.ExecuteAsync(null);
        fake.JavaTrustCopyBehavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Copy(); };
        var pending = tools.PrepareCopyCommand.ExecuteAsync(null); root.CancelOperationCommand.Execute(null); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(tools.HasPrepared); Assert.Equal(Source, fake.Snapshot.Settings.JavaExecutable); Assert.Contains("已取消", tools.Status);
        fake.JavaTrustCopyBehavior = (_, _) => Task.FromResult(Copy()); await tools.PrepareCopyCommand.ExecuteAsync(null);
        fake.JavaTrustSelectBehavior = (_, _) => throw new IOException("Changed copy password=fixture-private-marker");
        await tools.SelectCopyCommand.ExecuteAsync(null); Assert.Equal(Source, fake.Snapshot.Settings.JavaExecutable);
        Assert.False(tools.HasPrepared); Assert.DoesNotContain("fixture-private-marker", root.ErrorMessage); Assert.False(tools.HasRollback);
    }
    [AvaloniaFact]
    public async Task PreviousSelectionRemainsAvailableAfterReopeningTheLauncher()
    {
        var fake = Fixture(auto: true);
        using (var root = new MainWindowViewModel(fake, new FakeExternalLauncher()))
        { await Prepare(root); await root.Tools.JavaTrust.SelectCopyCommand.ExecuteAsync(null); }
        using var reopened = new MainWindowViewModel(fake, new FakeExternalLauncher());
        Assert.True(reopened.Tools.JavaTrust.HasRollback); Assert.Contains("自动", reopened.Tools.JavaTrust.RollbackLabel);
        await reopened.Tools.JavaTrust.RestoreSelectionCommand.ExecuteAsync(null);
        Assert.Null(fake.Snapshot.Settings.JavaExecutable); Assert.Null(fake.Snapshot.Settings.JavaTrustSelection);
    }
    [AvaloniaFact]
    public async Task RollbackRestoresPreviousExplicitChoiceOnlyAfterProbeOrRestoresAutomaticSelection()
    {
        foreach (var automatic in new[] { false, true })
        {
            var fake = Fixture(auto: automatic); using var root = new MainWindowViewModel(fake, new FakeExternalLauncher()); var tools = root.Tools.JavaTrust;
            await Prepare(root); await tools.SelectCopyCommand.ExecuteAsync(null); Assert.True(tools.HasRollback);
            if (!automatic)
            {
                fake.JavaTrustInspectBehavior = (_, _) => throw new IOException("Missing source"); await tools.RestoreSelectionCommand.ExecuteAsync(null);
                Assert.Equal(Copy().Java.ExecutablePath, fake.Snapshot.Settings.JavaExecutable); Assert.True(tools.HasRollback);
                fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([Finding()]);
            }
            await tools.RestoreSelectionCommand.ExecuteAsync(null);
            Assert.Equal(automatic ? null : Source, fake.Snapshot.Settings.JavaExecutable); Assert.False(tools.HasRollback);
        }
    }
}
