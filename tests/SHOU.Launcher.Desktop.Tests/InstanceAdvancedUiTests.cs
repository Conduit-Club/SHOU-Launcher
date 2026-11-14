using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class InstanceAdvancedUiTests
{
    [AvaloniaFact]
    public async Task ExplicitFolderOpenCanCreateTheNewlySelectedEmptyDirectory()
    {
        var service=new AdvancedService();var external=new FakeExternalLauncher();using var root=new MainWindowViewModel(service,external);
        await root.Instances.OpenInstanceFolderAsync(null);Assert.True(service.CreateDirectoryRequested);
    }
    [AvaloniaFact]
    public async Task AdvancedControlsBindPersistAndDirectoryChangeRequiresItsOwnConfirmation()
    {
        var service=new AdvancedService();using var root=new MainWindowViewModel(service,new FakeExternalLauncher());var vm=root.Instances;vm.ShowInstanceSettingsCommand.Execute(null);vm.SelectedSection=vm.Sections.Single(x=>x.Key=="settings");
        var view=new InstancesView{DataContext=vm};var window=new Window{Content=view,Width=976,Height=636};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            foreach(var id in new[]{"InstanceDirectoryModeSetting","InstanceClasspathSetting","InstanceBeforeExecutableSetting","InstanceInitialMemoryValue","InstanceDebugLoggingSetting"})Assert.NotNull(view.FindControl<Control>(id));
            vm.DirectoryMode=InstanceDirectoryMode.Shared;Assert.True(vm.DirectoryWillChange);await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);Assert.Equal(0,service.Edits);
            vm.DirectoryChangeConfirmed=true;vm.UseGlobalLaunchOptions=false;vm.InstanceInitialMemory=true;vm.InstanceInitialMemoryMiB=768;vm.InstanceDebugLogging=true;vm.InstanceBeforeArguments="--public";
            await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);Assert.Equal(1,service.Edits);Assert.Equal(768,service.LastEdit!.LaunchOptions!.MinimumMemoryMiB);Assert.True(service.LastEdit.ConfirmDirectoryChange);Assert.True(service.LastEdit.LaunchOptions.DebugLogging);
        }
        finally{window.Close();}
    }
    [AvaloniaFact]
    public async Task LegacyReadCreatesDraftAndNeverSavesAutomatically()
    {
        var service=new AdvancedService();using var root=new MainWindowViewModel(service,new FakeExternalLauncher());var vm=root.Instances;
        await vm.ReadLegacySettingsAsync();Assert.Equal("legacy title",vm.InstanceWindowTitle);Assert.Equal(InstanceDirectoryMode.Shared,vm.DirectoryMode);Assert.False(vm.UseGlobalLaunchOptions);Assert.Equal(0,service.Edits);Assert.Contains("manual review",vm.Feedback);
    }
    [AvaloniaFact]
    public async Task LegacyRuntimeDraftPopulatesJavaAndMemoryWithoutWritingBeforeConfirmation()
    {
        var java=Path.Combine(Path.GetTempPath(),"controlled-java","java.exe");
        var service=new AdvancedService{LegacyDraft=new(new(MinimumMemoryMiB:1024),null,[],java,8192)};
        using var root=new MainWindowViewModel(service,new FakeExternalLauncher());var vm=root.Instances;
        await vm.ReadLegacySettingsAsync();Assert.Equal(java,vm.InstanceJavaExecutable);Assert.False(vm.UseGlobalJava);Assert.Equal(8192,vm.InstanceMemoryMiB);Assert.False(vm.UseGlobalMemory);
        Assert.True(vm.InstanceInitialMemory);Assert.Equal(1024,vm.InstanceInitialMemoryMiB);Assert.Equal(0,service.Edits);
        await vm.SaveInstanceSettingsCommand.ExecuteAsync(null);Assert.Equal(1,service.Edits);Assert.Equal(java,service.LastEdit!.JavaExecutable);Assert.Equal(8192,service.LastEdit.MemoryMiB);
    }
    [AvaloniaFact]
    public async Task SavedInstanceRouteUsesCurrentAccountWithoutCreatingAnOfflineFallback()
    {
        var service=new FakeLauncherService();var id=service.Snapshot.Instances.Single().Id;await SavedInstanceLaunch.RunAsync(service,id,default);Assert.Equal("none",service.LastLaunchServerId);
        var empty=new FakeLauncherService();empty.Publish(empty.Snapshot with{Accounts=[],SelectedAccountId=null});await Assert.ThrowsAsync<InvalidOperationException>(()=>SavedInstanceLaunch.RunAsync(empty,id,default));Assert.Null(empty.LastLaunchServerId);Assert.Empty(empty.Snapshot.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SavedInstanceLaunch.RunAsync(empty,"missing",default));
    }
    [AvaloniaFact]
    public async Task ScriptExportDoesNotInvokeLaunchOrExposeAccountData()
    {
        var service=new AdvancedService();using var root=new MainWindowViewModel(service,new FakeExternalLauncher());await root.Instances.ExportScriptAsync("fixture","output.command",false);Assert.Equal(1,service.Exports);Assert.Null(service.LastLaunchServerId);Assert.Equal("启动脚本已保存。",root.Instances.Feedback);
    }
    private sealed class AdvancedService:FakeLauncherService,IInstanceWorkspaceService,ILaunchScriptService,ILegacyInstanceSettingsService,IInstanceDirectoryService
    {
        public int Edits,Exports;public InstanceEdit? LastEdit;
        public bool CreateDirectoryRequested;
        public LegacyInstanceSettings LegacyDraft=new(new(WindowTitle:"legacy title"),InstanceDirectoryMode.Shared,["manual review"]);
        public Task<string> GetInstanceDirectoryAsync(string id,string? kind=null,bool createMissing=false,CancellationToken cancellationToken=default){CreateDirectoryRequested=createMissing;return Task.FromResult(Path.GetTempPath());}
        public Task UpdateInstanceAsync(string id,InstanceEdit edit,CancellationToken cancellationToken=default){Edits++;LastEdit=edit;return Task.CompletedTask;}
        public Task<LegacyInstanceSettings> ReadLegacyInstanceSettingsAsync(string id,CancellationToken cancellationToken=default)=>Task.FromResult(LegacyDraft);
        public Task ExportLaunchScriptAsync(string id,string executable,string destination,bool overwrite=false,CancellationToken cancellationToken=default){Exports++;return Task.CompletedTask;}
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id,string kind,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<InstanceContentEntry>>([]);
        public Task ImportInstanceContentAsync(string id,string kind,string source,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task SetInstanceModEnabledAsync(string id,string relativePath,bool enabled,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<string> ExportInstanceAsync(string id,bool worlds,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
