using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

// Protocol fixtures only: these tests never run keytool or modify a Java installation.
public sealed class JavaTrustSettingsTests
{
    private static string FixturePath(string kind, string leaf) => Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "shou-ui-protocol-fixture", kind, leaf);
    private static string SourceJava => FixturePath("source", OperatingSystem.IsWindows() ? "bin/java.exe" : "bin/java");
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static MainWindow Open(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm }; window.Show(); vm.SelectedPage = vm.Settings; Pump(window); return window;
    }
    private static FakeLauncherService WithSelectedSource()
    {
        var fake = new FakeLauncherService();
        var source = fake.Snapshot.JavaInstallations[0] with { ExecutablePath = SourceJava, JavaHome = FixturePath("source", ""), ValidatedManifestSha1 = new string('1', 40) };
        fake.Publish(fake.Snapshot with { Settings = fake.Snapshot.Settings with { JavaExecutable = SourceJava }, JavaInstallations = [source] });
        return fake;
    }
    private static JavaTrustDerivation Derivation()
    {
        var home = FixturePath("derived", "");
        var java = new JavaInstallation(FixturePath("derived", OperatingSystem.IsWindows() ? "bin/java.exe" : "bin/java"), home,
            "21.0.13", 21, "Explicit protocol fixture", RuntimeArchitecture.Arm64, "derived-fixture");
        return new(java, SourceJava, "fixture-policy-v1", new string('a', 64), new string('b', 64),
            FixturePath("derived", "provenance.json"), [new(Path.Combine(home, "lib/security/cacerts"), true, new string('c', 64), false, true, "explicit fixture processing record")]);
    }

    [AvaloniaFact]
    public async Task AutoRepairPreferencePersistsAndTrustActionsRequireAnExplicitAbsoluteJava()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            Assert.True(Find<CheckBox>(window, "JavaTrustAutoRepairSetting").IsChecked);
            Assert.False(Find<Button>(window, "InspectJavaTrustButton").IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, "CreateRepairedJavaCopyButton").IsEffectivelyEnabled);
            Find<CheckBox>(window, "JavaTrustAutoRepairSetting").IsChecked = false; Pump(window);
            await vm.Settings.SaveCommand.ExecuteAsync(null); Pump(window);
            Assert.False(fake.Snapshot.Settings.ConduitJavaTrustAutoRepair); Assert.Equal(1, fake.SaveCalls);
            Find<TextBox>(window, "GlobalJavaExecutableSetting").Text = "bin/java"; Pump(window);
            Assert.False(vm.Settings.InspectJavaTrustCommand.CanExecute(null));
            await vm.Settings.InspectJavaTrustCommand.ExecuteAsync(null);
            await vm.Settings.CreateRepairedJavaCopyCommand.ExecuteAsync(null);
            Assert.Equal(0, fake.JavaTrustInspectCalls); Assert.Equal(0, fake.JavaTrustCopyCalls);
            Assert.True(vm.HasError);
            Find<TextBox>(window, "GlobalJavaExecutableSetting").Text = SourceJava; Pump(window);
            Assert.True(vm.Settings.InspectJavaTrustCommand.CanExecute(null));
            Assert.True(Find<Button>(window, "CreateRepairedJavaCopyButton").IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReadOnlyScanBindsFindingsAndFailureOrSelectionChangeClearsTheResult()
    {
        var fake = WithSelectedSource();
        fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>(
            [new(FixturePath("source", "lib/security/cacerts"), true, "fixture-hash", true, true, "explicit fixture finding")]);
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            Find<Button>(window, "InspectJavaTrustButton").Command!.Execute(null);
            await vm.Settings.InspectJavaTrustCommand.ExecutionTask!; Pump(window);
            Assert.Equal(SourceJava, fake.LastJavaTrustInspectPath); Assert.Single(vm.Settings.JavaTrustFindings);
            Assert.Equal(1, Find<ItemsControl>(window, "JavaTrustFindings").ItemCount);
            Assert.Contains("独立副本", vm.Settings.JavaTrustFindings[0].Summary);
            Assert.Equal(0, fake.SaveCalls); Assert.Equal(SourceJava, fake.Snapshot.Settings.JavaExecutable);
            fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>(
                [new(FixturePath("source", "missing-cacerts"), true, "", false, false, "未找到默认证书库，未能检查")]);
            await vm.Settings.InspectJavaTrustCommand.ExecuteAsync(null); Pump(window);
            Assert.Contains("无法确认", Find<TextBlock>(window, "JavaTrustStatusText").Text);
            Assert.Contains("结果未知", vm.Settings.JavaTrustFindings.Single().Summary);
            Assert.DoesNotContain("未报告需修复项", vm.Settings.JavaTrustStatus);
            Assert.Equal(0, fake.SaveCalls); Assert.Equal(SourceJava, fake.Snapshot.Settings.JavaExecutable);
            fake.JavaTrustInspectBehavior = (_, _) => throw new InvalidDataException("扫描失败 password=fixture-scan-password");
            await vm.Settings.InspectJavaTrustCommand.ExecuteAsync(null); Pump(window);
            Assert.Empty(vm.Settings.JavaTrustFindings); Assert.Contains("扫描失败或已取消", vm.Settings.JavaTrustStatus);
            Assert.DoesNotContain("fixture-scan-password", vm.ErrorMessage);
            vm.Settings.JavaExecutable = SourceJava + "-other";
            Assert.Contains("尚未扫描", vm.Settings.JavaTrustStatus); Assert.False(vm.Settings.HasJavaTrustProvenance);
            fake.JavaTrustInspectBehavior = (_, _) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([]);
            await vm.Settings.InspectJavaTrustCommand.ExecuteAsync(null); Pump(window);
            Assert.Contains("无法据此确认", vm.Settings.JavaTrustStatus); Assert.Empty(vm.Settings.JavaTrustFindings);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SuccessfulCopySelectsReturnedJavaAndPreservesOtherUnsavedSettings()
    {
        var fake = WithSelectedSource();
        var completion = new TaskCompletionSource<JavaTrustDerivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.JavaTrustCopyBehavior = (_, ct) => completion.Task.WaitAsync(ct);
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.Settings.Theme = "Dark";
            Find<Button>(window, "CreateRepairedJavaCopyButton").Command!.Execute(null); Pump(window);
            Assert.True(vm.IsBusy); Assert.False(Find<Button>(window, "InspectJavaTrustButton").IsEffectivelyEnabled);
            Assert.False(Find<CheckBox>(window, "JavaTrustAutoRepairSetting").IsEffectivelyEnabled);
            Assert.Equal(SourceJava, fake.Snapshot.Settings.JavaExecutable);
            var result = Derivation(); completion.SetResult(result);
            await vm.Settings.CreateRepairedJavaCopyCommand.ExecutionTask!; Pump(window);
            Assert.Equal(SourceJava, fake.LastJavaTrustCopyPath);
            Assert.Equal(result.Java.ExecutablePath, vm.Settings.JavaExecutable);
            Assert.Equal(result.Java.ExecutablePath, fake.Snapshot.Settings.JavaExecutable);
            Assert.Contains(fake.Snapshot.JavaInstallations, j => j.ExecutablePath == SourceJava);
            Assert.Equal("Dark", vm.Settings.Theme); Assert.Equal("System", fake.Snapshot.Settings.Theme); Assert.True(vm.Settings.IsDirty);
            Assert.Equal(0, fake.SaveCalls);
            Assert.True(Find<TextBox>(window, "JavaTrustProvenanceText").IsVisible);
            Assert.Contains(result.PolicyId, vm.Settings.JavaTrustProvenance); Assert.Contains(result.ProvenancePath, vm.Settings.JavaTrustProvenance);
            Assert.Contains("原始 Java 未修改", vm.Settings.JavaTrustStatus);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledOrFailedCopyNeverChangesSelectionOrShowsSuccess()
    {
        var fake = WithSelectedSource();
        fake.JavaTrustCopyBehavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Derivation(); };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            Find<Button>(window, "CreateRepairedJavaCopyButton").Command!.Execute(null); Pump(window);
            Assert.True(vm.CancelOperationCommand.CanExecute(null)); vm.CancelOperationCommand.Execute(null);
            await vm.Settings.CreateRepairedJavaCopyCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5)); Pump(window);
            Assert.Equal(SourceJava, vm.Settings.JavaExecutable); Assert.Equal(SourceJava, fake.Snapshot.Settings.JavaExecutable);
            Assert.Contains("创建失败或已取消", vm.Settings.JavaTrustStatus); Assert.False(vm.Settings.HasJavaTrustProvenance);
            fake.JavaTrustCopyBehavior = (_, _) => throw new IOException("复制失败 password=fixture-copy-password");
            await vm.Settings.CreateRepairedJavaCopyCommand.ExecuteAsync(null); Pump(window);
            Assert.True(vm.HasError); Assert.DoesNotContain("fixture-copy-password", vm.ErrorMessage);
            Assert.Equal(SourceJava, fake.Snapshot.Settings.JavaExecutable); Assert.Equal(SourceJava, vm.Settings.JavaExecutable);
            Assert.False(vm.Settings.HasJavaTrustProvenance); Assert.Empty(vm.Settings.JavaTrustFindings); Assert.Equal(0, fake.SaveCalls);
        }
        finally { window.Close(); }
    }
}
