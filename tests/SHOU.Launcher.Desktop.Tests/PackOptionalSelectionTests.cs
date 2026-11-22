using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class PackOptionalSelectionTests
{
    private const string One = "cf:12:2", Manual = "cf:13:3";
    [AvaloniaFact]
    public async Task LocalImportChecksTheChosenSubsetAndPreservesNameAndPreparationOption()
    {
        var service = new OptionalFixture(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); Assert.Equal(2, vm.Downloads.OptionalPackFiles.Count);
        Assert.All(vm.Downloads.OptionalPackFiles, f => Assert.False(f.Selected)); Assert.Empty(service.Imports);
        vm.Downloads.PackInstanceName = "Chosen name"; vm.Downloads.PreparePackAfterImport = true;
        vm.Downloads.OptionalPackFiles[0].Selected = true; Assert.Equal("检查选择并导入",vm.Downloads.LocalPackImportAction);
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
        var request = Assert.Single(service.Imports); Assert.Equal(new[] { One }, request.OptionalClientFiles);
        Assert.Equal("Chosen name", request.InstanceName); Assert.True(request.PrepareAfterImport);
        Assert.Equal(new[] { One }, service.InspectedSelections.Last()); Assert.Empty(service.Provided); Assert.Null(service.LastLaunchServerId);
    }
    [AvaloniaFact]
    public async Task SelectedManualOptionalWaitsForExactFileAndExplicitFinalImport()
    {
        var service = new OptionalFixture(); using var vm = new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip"); vm.Downloads.PackInstanceName="Manual draft"; vm.Downloads.PreparePackAfterImport=true;
        vm.Downloads.OptionalPackFiles[1].Selected=true; await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
        Assert.Empty(service.Imports); Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
        var row=Assert.Single(vm.Downloads.LocalPackManualFiles); await row.ProvideFileAsync("/fixture/wrong.jar");
        Assert.Single(vm.Downloads.LocalPackManualFiles); Assert.Empty(service.Provided);
        await row.ProvideFileAsync("/fixture/correct.jar");
        Assert.Empty(vm.Downloads.LocalPackManualFiles); Assert.Empty(service.Imports);
        Assert.True(vm.Downloads.OptionalPackFiles[1].Selected); Assert.Equal("Manual draft",vm.Downloads.PackInstanceName); Assert.True(vm.Downloads.PreparePackAfterImport);
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
        Assert.Equal(new[]{Manual},Assert.Single(service.Imports).OptionalClientFiles); Assert.Equal(new[]{Manual},Assert.Single(service.Provided));
    }
    [AvaloniaFact]
    public async Task DeselectingManualOptionalInvalidatesItsPickerAndCanImportWithoutIt()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");vm.Downloads.OptionalPackFiles[1].Selected=true;
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);var stale=Assert.Single(vm.Downloads.LocalPackManualFiles);
        vm.Downloads.OptionalPackFiles[1].Selected=false;await stale.ProvideFileAsync("/fixture/correct.jar");
        Assert.False(stale.CanProvide);Assert.Empty(service.Provided);
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);Assert.Empty(Assert.Single(service.Imports).OptionalClientFiles!);
    }
    [AvaloniaFact]
    public async Task CancelledSelectionCheckKeepsTheDraftAndLateResponseCannotImport()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");vm.Downloads.PackInstanceName="Keep draft";vm.Downloads.OptionalPackFiles[0].Selected=true;
        service.SelectionDelay=async _=>{entered.SetResult();await release.Task;};
        var pending=vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);await entered.Task;vm.CancelOperationCommand.Execute(null);release.SetResult();await pending;
        Assert.Empty(service.Imports);Assert.True(vm.Downloads.OptionalPackFiles[0].Selected);Assert.Equal("Keep draft",vm.Downloads.PackInstanceName);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));service.SelectionDelay=null;
        await vm.Downloads.RecheckLocalPackCommand.ExecuteAsync(null);await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
        Assert.Equal(new[]{One},Assert.Single(service.Imports).OptionalClientFiles);
    }
    [AvaloniaFact]
    public async Task SwitchingArchivesIgnoresOldChoicesAndChangedArchiveNeedsASeparateConfirmation()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());
        await vm.Downloads.ReadLocalPackAsync("/fixture/a.zip");var stale=vm.Downloads.OptionalPackFiles[0];
        await vm.Downloads.ReadLocalPackAsync("/fixture/b.zip");stale.Selected=true;Assert.All(vm.Downloads.OptionalPackFiles,f=>Assert.False(f.Selected));
        vm.Downloads.OptionalPackFiles[0].Selected=true;service.ChangeHash=true;
        await vm.Downloads.ImportLocalPackCommand.ExecuteAsync(null);Assert.Empty(service.Imports);Assert.False(vm.Downloads.HasPackInspection);
        Assert.False(vm.Downloads.ImportLocalPackCommand.CanExecute(null));
    }
    [AvaloniaTheory,InlineData(false),InlineData(true)]
    public async Task BackgroundTaskWaitsForAChoiceThenInstallsOnlyTheConfirmedSubset(bool select)
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());
        var window=new MainWindow{Width=880,Height=560,DataContext=vm};window.Show();
        try
        {
            var task=await Start(vm,service);Assert.True(task.HasPendingOptionalSelection);Assert.False(task.HasError);Assert.Empty(service.Imports);Assert.False(vm.IsBusy);
            vm.ViewTaskNotificationCommand.Execute(null);Dispatcher.UIThread.RunJobs();window.UpdateLayout();Dispatcher.UIThread.RunJobs();
            var panel=window.GetVisualDescendants().OfType<StackPanel>().Single(p=>p.Name=="PackTaskOptionalFilesPanel"&&p.IsEffectivelyVisible);
            Assert.Equal(2,panel.GetVisualDescendants().OfType<CheckBox>().Count(c=>c.Name=="PackOptionalFileChoice"));
            var next=panel.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ConfirmPackOptionalFilesButton");Assert.True(next.IsEffectivelyEnabled);
            if(select)task.OptionalFiles[0].Selected=true;
            await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);Assert.True(task.IsCompleted);Assert.False(task.HasPendingOptionalSelection);
            Assert.Equal(select?new[]{One}:[],Assert.Single(service.Imports).OptionalClientFiles);Assert.Equal(1,service.Prepared);Assert.Null(service.LastLaunchServerId);
        }
        finally{window.Close();}
    }
    [AvaloniaFact]
    public async Task BackgroundManualChoiceCanBeRemovedAndItsStalePickerCannotResumeTheOldSelection()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);
        task.OptionalFiles[1].Selected=true;await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);var stale=Assert.Single(task.ManualFiles);
        Assert.Empty(service.Imports);task.OptionalFiles[1].Selected=false;await stale.ProvideFileAsync("/fixture/correct.jar");Assert.Empty(service.Provided);
        await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);Assert.True(task.IsCompleted);Assert.Empty(Assert.Single(service.Imports).OptionalClientFiles!);
    }
    [AvaloniaFact]
    public async Task BackgroundSelectedManualFileResumesExactlyOnceWithTheConfirmedSelection()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);
        task.OptionalFiles[1].Selected=true;await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);var row=Assert.Single(task.ManualFiles);
        await row.ProvideFileAsync("/fixture/wrong.jar");Assert.Single(task.ManualFiles);Assert.Empty(service.Imports);
        await row.ProvideFileAsync("/fixture/correct.jar");Assert.True(task.IsCompleted);Assert.Equal(new[]{Manual},Assert.Single(service.Imports).OptionalClientFiles);
        Assert.Equal(1,service.Prepared);await row.ProvideFileAsync("/fixture/correct.jar");Assert.Single(service.Imports);Assert.Single(service.Provided);
    }
    [AvaloniaFact]
    public async Task ChangedBackgroundArchiveClearsOldChoicesAndWaitsForAnotherConfirmation()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);
        task.OptionalFiles[0].Selected=true;service.ChangeHash=true;await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);
        Assert.True(task.HasPendingOptionalSelection);Assert.All(task.OptionalFiles,f=>Assert.False(f.Selected));Assert.Empty(service.Imports);
        await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);Assert.True(task.IsCompleted);Assert.Empty(Assert.Single(service.Imports).OptionalClientFiles!);
    }
    [AvaloniaFact]
    public async Task BackgroundWaitingChoiceCanCancelRetryAndKeepItsChoicesWithoutImporting()
    {
        var service=new OptionalFixture();using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);
        task.OptionalFiles[0].Selected=true;task.CancelCommand.Execute(null);Assert.Equal("已取消",task.State);Assert.Empty(service.Imports);
        await task.RetryCommand.ExecuteAsync(null);Assert.True(task.HasPendingOptionalSelection);Assert.True(task.OptionalFiles[0].Selected);Assert.Empty(service.Imports);
        await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);Assert.Equal(new[]{One},Assert.Single(service.Imports).OptionalClientFiles);
    }
    [AvaloniaFact]
    public async Task BackgroundMrpackAlsoWaitsForItsOptionalFilesAndPassesTheirPaths()
    {
        var service=new OptionalFixture{Mrpack=true};using var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);
        Assert.Equal("mods/one.jar",task.OptionalFiles[0].Path);task.OptionalFiles[0].Selected=true;
        await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);Assert.Equal(new[]{"mods/one.jar"},Assert.Single(service.Imports).OptionalClientFiles);Assert.True(task.IsCompleted);
    }
    [AvaloniaFact]
    public async Task DisposedBackgroundChoiceCannotImportOrAcceptOldFiles()
    {
        var service=new OptionalFixture();var vm=new MainWindowViewModel(service,new FakeExternalLauncher());var task=await Start(vm,service);vm.Dispose();
        Assert.False(task.ConfirmOptionalFilesCommand.CanExecute(null));await task.ConfirmOptionalFilesCommand.ExecuteAsync(null);await task.RetryCommand.ExecuteAsync(null);Assert.Empty(service.Imports);
    }
    private static async Task<PackInstallTaskViewModel> Start(MainWindowViewModel vm,OptionalFixture service)
    {
        vm.ShowDownloadsCommand.Execute(null);vm.Downloads.SelectCategory("modpack");await vm.Downloads.OpenProjectAsync(OptionalFixture.Project);
        var file=vm.Downloads.ReleaseGroups.Single().Releases.Single().Files.Single();await file.DownloadCommand.ExecuteAsync(null);return Assert.Single(vm.Downloads.PackTasks);
    }
    private sealed class OptionalFixture:FakeLauncherService,ILauncherCatalogService,ILocalPackService,ILocalPackSelectionService,ILocalPackManualFileService
    {
        public bool Mrpack,ChangeHash;public int Prepared;
        public Func<CancellationToken,Task>? SelectionDelay;
        private readonly HashSet<string> supplied=[];
        public List<LocalPackImportRequest> Imports{get;}=[];
        public List<string[]> InspectedSelections{get;}=[];
        public List<string[]> Provided{get;}=[];
        public OptionalFixture(){InstallBehavior=_=>{Prepared++;return Task.CompletedTask;};}
        public static ContentProject Project=>new("cf-1","fixture","Optional fixture","Test only","Fixture",1,"modpack",null,new("https://www.curseforge.com/projects/1"),"curseforge");
        public Task<LocalPackInspection> InspectLocalPackAsync(string path,CancellationToken cancellationToken=default)=>Inspect(path,[],cancellationToken);
        public Task<LocalPackInspection> InspectLocalPackSelectionAsync(string path,IReadOnlyList<string> optionalFiles,IProgress<PackInstallProgress>? progress=null,CancellationToken cancellationToken=default,string? expectedArchiveSha256=null)=>Inspect(path,optionalFiles,cancellationToken);
        private async Task<LocalPackInspection> Inspect(string path,IReadOnlyList<string> selected,CancellationToken ct)
        {
            if(selected.Count>0&&SelectionDelay is not null)await SelectionDelay(ct);InspectedSelections.Add(selected.ToArray());
            var hash=new string(ChangeHash?'c':path.EndsWith("b.zip",StringComparison.Ordinal)?'b':'a',64);
            var pending=selected.Contains(Manual)&&!supplied.Contains(path);
            var choices=new[]{new LocalPackOptionalFile(One,"Optional one v2.0.jar",new("https://www.curseforge.com/projects/12/files/2")),new LocalPackOptionalFile(Manual,"Optional manual v3.0.jar",new("https://www.curseforge.com/projects/13/files/3"))};
            var mr=Mrpack?new ModrinthPackInspection("Fixture","1","1.21.1","fabric","0.16.14",hash,[new("mods/one.jar",new('a',40),new('b',128),1,[],"optional")],[],[],true):null;
            return new(Mrpack?LocalPackFormat.Modrinth:LocalPackFormat.CurseForge,"Fixture","1.21.1","fabric","0.16.14",hash,[],!pending,pending?"manual optional pending":null,mr,
                ManualFiles:pending?[new(13,3,"Optional manual v3.0.jar",new('a',40),1,new("https://www.curseforge.com/projects/13/files/3"))]:[],OptionalFiles:Mrpack?[]:choices,SelectedOptionalFiles:selected.ToArray());
        }
        public Task ProvideSelectedLocalPackFileAsync(string path,string sha,IReadOnlyList<string> optionalFiles,int projectId,int fileId,string source,CancellationToken cancellationToken=default)
        {if(!optionalFiles.Contains(Manual)||source.EndsWith("wrong.jar",StringComparison.Ordinal))throw new InvalidDataException("Wrong selected file SHA-1");Provided.Add(optionalFiles.ToArray());supplied.Add(path);return Task.CompletedTask;}
        public Task ProvideLocalPackFileAsync(string path,string sha,int projectId,int fileId,string source,CancellationToken cancellationToken=default)=>throw new InvalidDataException("Optional needs a selection.");
        public Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();Imports.Add(request);var i=new GameInstance("optional-"+Imports.Count,request.InstanceName,"1.21.1",Paths.InstancesDirectory+"/optional-"+Imports.Count);Publish(Snapshot with{Instances=[..Snapshot.Instances,i],SelectedInstanceId=i.Id});return Task.FromResult(new LocalPackImportResult(i,1,[]));}
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct=default)=>Task.FromResult(new GameCatalog([],"fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string v,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery q,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ContentProject>>([Project]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id,string? gameVersion=null,string? loader=null,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ContentRelease>>([new("1","Fixture","1",DateTimeOffset.UnixEpoch,["1.21.1"],["fabric"],[new("fixture.zip",new("https://edge.forgecdn.net/files/1/2/fixture.zip"),new('a',40),1)])]);
        public Task<string> DownloadContentAsync(ContentFile f,CancellationToken ct=default)=>Task.FromResult("/fixture/a.zip");
        public Task<byte[]> GetContentIconAsync(string url,CancellationToken ct=default)=>Task.FromResult(Array.Empty<byte>());
    }
}
