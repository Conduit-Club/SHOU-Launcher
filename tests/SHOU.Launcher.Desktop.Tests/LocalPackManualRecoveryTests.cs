using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LocalPackManualRecoveryTests
{
    [AvaloniaFact]
    public async Task LocalFilesRejectWrongBytesRecheckEverySuccessAndWaitForExplicitImport()
    {
        var service = new RecoveryFixture(); var browser = new FakeExternalLauncher();
        using var vm = new MainWindowViewModel(service, browser);
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");
        var rows = vm.Downloads.LocalPackManualFiles;
        Assert.Equal(2, rows.Count); Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        vm.Downloads.PackInstanceName = "My chosen name"; vm.Downloads.PreparePackAfterImport = true;
        await rows[0].OpenPageCommand.ExecuteAsync(null); Assert.Equal(rows[0].File.PageUri, Assert.Single(browser.OpenedUris));
        await rows[0].ProvideFileAsync("/fixture/wrong.jar");
        Assert.Equal(2, rows.Count); Assert.Contains("SHA-1", vm.Downloads.PackStatus); Assert.Empty(service.Imports);
        var stale = rows[0]; await stale.ProvideFileAsync("/fixture/correct.jar");
        Assert.Single(rows); Assert.False(stale.CanProvide); Assert.Equal(2, rows[0].File.FileId);
        Assert.DoesNotContain("missing-1", vm.Downloads.PackDetails); Assert.Contains("missing-2", vm.Downloads.PackDetails);
        Assert.Equal("My chosen name", vm.Downloads.PackInstanceName); Assert.True(vm.Downloads.PreparePackAfterImport);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        await rows[0].ProvideFileAsync("/fixture/correct.jar");
        Assert.Empty(rows); Assert.Empty(service.Imports); Assert.True(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
        var imported = Assert.Single(service.Imports); Assert.Equal("My chosen name", imported.InstanceName);
        Assert.True(imported.PrepareAfterImport); Assert.Equal(new string('a',64), imported.ExpectedSha256);
        Assert.Equal(3, service.Inspections); Assert.Null(service.LastLaunchServerId);
    }
    [AvaloniaFact]
    public async Task ARowFromAnotherArchiveCannotProvideOrOpenItsWebsite()
    {
        var service = new RecoveryFixture(); var browser = new FakeExternalLauncher();
        using var vm = new MainWindowViewModel(service, browser);
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); var previous = vm.Downloads.LocalPackManualFiles[0];
        await vm.Downloads.ReadLocalPackAsync("/fixture/b.zip");
        await previous.ProvideFileAsync("/fixture/correct.jar"); await previous.OpenPageCommand.ExecuteAsync(null);
        Assert.False(previous.CanProvide); Assert.Empty(service.Provided); Assert.Empty(browser.OpenedUris);
        Assert.Equal("/fixture/b.zip", vm.Downloads.LocalPackPath); Assert.Equal(2, vm.Downloads.LocalPackManualFiles.Count);
    }
    [AvaloniaFact]
    public async Task AChangedArchiveAfterProvidingCannotBecomeAnInstallableDraft()
    {
        var service = new RecoveryFixture { ChangeOnProvide = true };
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); vm.Downloads.PackInstanceName = "Keep my name";
        await vm.Downloads.LocalPackManualFiles[0].ProvideFileAsync("/fixture/correct.jar");
        Assert.Contains("已改变", vm.Downloads.PackStatus); Assert.False(vm.Downloads.HasPackInspection);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null)); Assert.Equal("Keep my name", vm.Downloads.PackInstanceName);
        Assert.Empty(service.Imports);
    }
    [AvaloniaFact]
    public async Task CancelledSupplyAndLateResultsLeaveTheDraftPending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecoveryFixture { ProvideDelay = async _ => { entered.SetResult(); await release.Task; } };
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); var row = vm.Downloads.LocalPackManualFiles[0];
        var pending = row.ProvideFileAsync("/fixture/correct.jar"); await entered.Task;
        vm.CancelOperationCommand.Execute(null); release.SetResult(); await pending;
        Assert.Equal(2, vm.Downloads.LocalPackManualFiles.Count); Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        Assert.Contains("取消", vm.Downloads.PackStatus); Assert.Null(vm.ErrorMessage); Assert.Empty(service.Imports);
        await vm.Downloads.RecheckLocalPackCommand.ExecuteAsync(null);
        Assert.Single(vm.Downloads.LocalPackManualFiles); Assert.Equal(2, vm.Downloads.LocalPackManualFiles[0].File.FileId);
    }
    [AvaloniaFact]
    public async Task RecheckFailureKeepsRemainingRowsAndManualRetryPreservesNameAndOptions()
    {
        var service = new RecoveryFixture(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); vm.Downloads.PackInstanceName = "Recovery draft"; vm.Downloads.PreparePackAfterImport = true;
        service.FailInspection = true; await vm.Downloads.LocalPackManualFiles[0].ProvideFileAsync("/fixture/correct.jar");
        var row = Assert.Single(vm.Downloads.LocalPackManualFiles); Assert.Equal(2,row.File.FileId); Assert.True(row.CanProvide);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null)); Assert.Contains("unavailable",vm.Downloads.PackStatus);
        service.FailInspection = false; await vm.Downloads.RecheckLocalPackCommand.ExecuteAsync(null);
        Assert.Equal("Recovery draft",vm.Downloads.PackInstanceName); Assert.True(vm.Downloads.PreparePackAfterImport);
        await vm.Downloads.LocalPackManualFiles[0].ProvideFileAsync("/fixture/correct.jar");
        Assert.True(vm.Downloads.ImportLocalPackCommand.CanExecute(null)); Assert.Empty(service.Imports);
    }
    [AvaloniaFact]
    public async Task OtherUnresolvedDependenciesStillBlockInstallationWhenManualFilesAreDone()
    {
        var service = new RecoveryFixture { OtherBlocker = true }; using var vm = new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");
        await vm.Downloads.LocalPackManualFiles[0].ProvideFileAsync("/fixture/correct.jar");
        await vm.Downloads.LocalPackManualFiles[0].ProvideFileAsync("/fixture/correct.jar");
        Assert.Empty(vm.Downloads.LocalPackManualFiles); Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        Assert.Contains("other unresolved dependency",vm.Downloads.PackStatus); Assert.Empty(service.Imports);
    }
    [AvaloniaFact]
    public async Task CancelledRecheckPreservesMrpackOptionalSelectionAndRequiresSuccessfulPreflight()
    {
        var service = new RecoveryFixture { Mrpack = true }; using var vm = new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.mrpack"); vm.Downloads.OptionalPackFiles[0].Selected=true; vm.Downloads.PackInstanceName="MR draft";
        service.InspectDelay = ct => Task.Delay(Timeout.Infinite,ct);
        var pending = vm.Downloads.RecheckLocalPackCommand.ExecuteAsync(null); vm.CancelOperationCommand.Execute(null); await pending;
        Assert.True(Assert.Single(vm.Downloads.OptionalPackFiles).Selected); Assert.Equal("MR draft",vm.Downloads.PackInstanceName);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        service.InspectDelay=null; await vm.Downloads.RecheckLocalPackCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(vm.Downloads.OptionalPackFiles).Selected); Assert.True(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task PendingRowsUseTheSameWrappingTemplateAtTheMinimumWindowSize()
    {
        var service = new RecoveryFixture { LongNames = true }; using var vm = new MainWindowViewModel(service,new FakeExternalLauncher());
        var window=new MainWindow {Width=880,Height=560,DataContext=vm};window.Show();
        try
        {
            vm.ShowDownloadsCommand.Execute(null);vm.Downloads.SelectCategory("modpack");await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();Dispatcher.UIThread.RunJobs();
            var panel=window.GetVisualDescendants().OfType<StackPanel>().Single(c=>c.Name=="LocalPackManualFilesPanel"&&c.IsEffectivelyVisible);
            var buttons=panel.GetVisualDescendants().OfType<Button>().Where(b=>b.Name=="ProvidePackManualFileButton").ToArray();
            Assert.Equal(2,buttons.Length);
            Assert.All(buttons,b=>{Assert.True(b.IsEffectivelyEnabled);Assert.True(b.Bounds.Width>0&&b.Bounds.Width<=panel.Bounds.Width);});
            Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ImportModrinthPackButton"&&b.IsEffectivelyVisible).IsEffectivelyEnabled);
            Assert.Null(service.LastLaunchServerId);
        }
        finally{window.Close();}
    }
    private sealed class RecoveryFixture : FakeLauncherService, ILauncherCatalogService, ILocalPackService, ILocalPackManualFileService
    {
        public bool ChangeOnProvide, FailInspection, OtherBlocker, Mrpack, LongNames;
        public int Inspections; private bool changed;
        public Func<CancellationToken,Task>? ProvideDelay,InspectDelay;
        public List<(string Archive,int File)> Provided {get;}=[];
        public List<LocalPackImportRequest> Imports {get;}=[];
        public async Task<LocalPackInspection> InspectLocalPackAsync(string path,CancellationToken cancellationToken=default)
        {
            Inspections++; if(InspectDelay is not null)await InspectDelay(cancellationToken);
            if(FailInspection)throw new IOException("official metadata unavailable");
            var hash=new string(changed?'c':path.EndsWith("b.zip",StringComparison.Ordinal)?'b':'a',64);
            var pending=new[]{1,2}.Where(id=>!Provided.Contains((path,id))).Select(id=>new CurseForgeManualFile(10,id,
                (LongNames?new string('x',100):"missing-")+id+".jar",new('d',40),1024,new("https://www.curseforge.com/projects/10/files/"+id))).ToArray();
            var mr=Mrpack?new ModrinthPackInspection("MR fixture","1","1.21.1","fabric","0.16.14",hash,[new("mods/optional.jar",new('a',40),new('b',128),10,[],"optional")],[],[],true):null;
            return new(Mrpack?LocalPackFormat.Modrinth:LocalPackFormat.CurseForge,"Fixture","1.21.1","fabric","0.16.14",hash,
                pending.Select(f=>f.FileName).ToArray(),Mrpack||pending.Length==0&&!OtherBlocker,
                OtherBlocker?"other unresolved dependency":"pending required files",mr,ManualFiles:Mrpack?[]:pending);
        }
        public async Task ProvideLocalPackFileAsync(string archivePath,string expectedArchiveSha256,int projectId,int fileId,string sourcePath,CancellationToken cancellationToken=default)
        {
            if(sourcePath.EndsWith("wrong.jar",StringComparison.Ordinal))throw new InvalidDataException("Wrong SHA-1; original file preserved");
            if(ProvideDelay is not null)await ProvideDelay(cancellationToken);
            Assert.Equal(10,projectId);Assert.Equal(new string(archivePath.EndsWith("b.zip",StringComparison.Ordinal)?'b':'a',64),expectedArchiveSha256);
            Provided.Add((archivePath,fileId));changed=ChangeOnProvide;
        }
        public Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request,CancellationToken cancellationToken=default)
        {Imports.Add(request);return Task.FromResult(new LocalPackImportResult(new("fixture",request.InstanceName,"1.21.1",Paths.InstancesDirectory+"/fixture"),1,[]));}
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct=default)=>Task.FromResult(new GameCatalog([],"fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string v,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery q,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id,string? gameVersion=null,string? loader=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile f,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string url,CancellationToken ct=default)=>Task.FromResult(Array.Empty<byte>());
    }
}
