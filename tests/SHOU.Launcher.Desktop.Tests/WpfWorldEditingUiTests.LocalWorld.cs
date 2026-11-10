using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed partial class WpfWorldEditingUiTests
{
    private static string LocalPath(Fixture f) => Path.Combine(f.Service.Paths.DataDirectory, "本地 世界.zip");
    private static LocalWorldImportPreview LocalPreview(LocalWorldImportRequest request) => new(new("late-local", "world", "One", request.WorldDirectoryName, "1.21.1", ["level.dat"], []), "本地世界", "world.zip", new string('a', 64));
    [AvaloniaFact]
    public async Task LocalWorldActualControlsRequireInspectThenIndependentConfirmation()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); var before = File.ReadAllBytes(f.LevelOne);
        Assert.True(Find<Button>(f.Window, "ImportLocalWorldButton").IsEffectivelyEnabled); Assert.True(Find<Button>(f.Window, "PasteLocalWorldButton").IsEffectivelyEnabled);
        Assert.True(f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory)); Pump(f.Window);
        Assert.True(Find<Border>(f.Window, "LocalWorldImportPanel").IsEffectivelyVisible); Assert.Equal(0, f.Service.LocalPreviews); Assert.Equal(0, f.Service.LocalImports);
        Find<TextBox>(f.Window, "LocalWorldDirectoryName").Text = "导入 世界"; Pump(f.Window);
        Assert.False(f.Vm.ConfirmLocalWorldImportCommand.CanExecute(null));
        await f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.True(Find<Button>(f.Window, "ConfirmLocalWorldImportButton").IsEffectivelyVisible); Assert.Contains("Local fixture world", f.Vm.LocalWorldPreviewSummary);
        Assert.Contains("导入 世界", f.Vm.LocalWorldPreviewSummary); Assert.Equal(0, f.Service.LocalImports); Assert.Equal(before, File.ReadAllBytes(f.LevelOne));
        await f.Vm.ConfirmLocalWorldImportCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.Equal(1, f.Service.LocalImports); Assert.Contains("已导入", f.Vm.LocalWorldImportStatus); Assert.False(f.Vm.HasLocalWorldPreview);
        Assert.Equal("one", f.Service.LastLocalRequest?.InstanceId); Assert.Equal("one", f.Service.Snapshot.SelectedInstanceId); Assert.Equal(before, File.ReadAllBytes(f.LevelOne));
    }
    [AvaloniaFact]
    public async Task LocalWorldLoadingIsInlineAndCancelStopsInspectionWithoutImporting()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync();
        var pending = new TaskCompletionSource<LocalWorldImportPreview>(); f.Service.LocalPreview = (_, ct) => pending.Task.WaitAsync(ct);
        f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory);
        var read = f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.True(f.Root.IsBusy); Assert.False(f.Root.ShowOperationStatus); Assert.True(Find<LoadingGlyph>(f.Window, "LocalWorldImportLoading").IsEffectivelyVisible);
        Assert.False(Find<TextBox>(f.Window, "LocalWorldDirectoryName").IsEffectivelyEnabled);
        Click(f.Window, "CancelLocalWorldImportButton"); await read; Pump(f.Window);
        Assert.False(f.Root.IsBusy); Assert.False(f.Vm.IsLocalWorldImportOpen); Assert.False(f.Vm.HasLocalWorldPreview); Assert.Equal(0, f.Service.LocalImports);
    }
    [AvaloniaFact]
    public async Task LateLocalWorldPreviewCannotBindToAnotherInstance()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); var pending = new TaskCompletionSource<LocalWorldImportPreview>();
        f.Service.LocalPreview = (_, _) => pending.Task; f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory);
        var read = f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null);
        f.Service.Base.Publish(f.Service.Snapshot with { SelectedInstanceId = "two" }); Pump(f.Window);
        pending.SetResult(LocalPreview(f.Service.LastLocalRequest!)); await read; Pump(f.Window);
        Assert.False(f.Vm.HasLocalWorldPreview); Assert.False(f.Vm.IsLocalWorldImportOpen); Assert.Contains("late-local", f.Service.DiscardedLocalPreviews); Assert.Equal(0, f.Service.LocalImports);
    }
    [AvaloniaFact]
    public async Task LocalWorldFolderChangeInvalidatesPreviewAndEscapeClosesDraft()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory);
        await f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null); Assert.True(f.Vm.HasLocalWorldPreview);
        f.Vm.LocalWorldDirectoryName = "different"; Assert.False(f.Vm.HasLocalWorldPreview); Assert.Contains("local-1", f.Service.DiscardedLocalPreviews);
        Assert.False(f.Vm.ConfirmLocalWorldImportCommand.CanExecute(null));
        ((Control)f.Window.Content!).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Pump(f.Window);
        Assert.False(f.Vm.IsLocalWorldImportOpen); Assert.Equal(0, f.Service.LocalImports);
    }
    [AvaloniaFact]
    public async Task LocalWorldRejectsMultipleFilesUrlsAndCapturedTargetChange()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); var game = f.Vm.SelectedInstance!.GameDirectory;
        Assert.False(f.Vm.SelectLocalWorldArchive([LocalPath(f), LocalPath(f)], "one", game));
        Assert.False(f.Vm.SelectLocalWorldArchive(["https://example.com/world.zip"], "one", game));
        Assert.False(f.Vm.SelectLocalWorldArchive([LocalPath(f)], "two", game));
        Assert.False(f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", game + "-changed"));
        Assert.False(f.Vm.IsLocalWorldImportOpen); Assert.Equal(0, f.Service.LocalPreviews); Assert.Equal(0, f.Service.LocalImports);
    }
    [AvaloniaFact]
    public async Task LocalWorldInspectionFailureLeavesSelectionRetryableAndDoesNotImport()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory);
        f.Service.LocalPreview = (_, _) => throw new IOException("controlled invalid world");
        await f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null);
        Assert.Contains("controlled invalid world", f.Vm.LocalWorldImportStatus); Assert.False(f.Vm.HasLocalWorldPreview); Assert.True(f.Vm.PreviewLocalWorldImportCommand.CanExecute(null)); Assert.Equal(0, f.Service.LocalImports);
        f.Service.LocalPreview = null; await f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null); Assert.True(f.Vm.HasLocalWorldPreview);
    }
    [AvaloniaFact]
    public async Task LocalWorldCancelAfterCompletedPublicationReportsCompletionInsteadOfFalseRollback()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); f.Vm.SelectLocalWorldArchive([LocalPath(f)], "one", f.Vm.SelectedInstance!.GameDirectory);
        await f.Vm.PreviewLocalWorldImportCommand.ExecuteAsync(null);
        f.Service.LocalImport = (_, _) => { f.Vm.CancelLocalWorldImportCommand.Execute(null); return Task.FromResult(new WorldContentResult("one", "completed", 1, "/controlled")); };
        await f.Vm.ConfirmLocalWorldImportCommand.ExecuteAsync(null);
        Assert.True(f.Vm.IsLocalWorldImportOpen); Assert.Contains("已完成导入", f.Vm.LocalWorldImportStatus); Assert.Equal(1, f.Service.LocalImports); Assert.False(f.Vm.HasLocalWorldPreview);
    }
}
