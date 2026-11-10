using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed partial class AccountAppearanceUiTests
{
    private static AppearanceUiService MicrosoftAppearanceService()
    {
        var service = new AppearanceUiService(); var account = new AccountProfile("microsoft:fixture", AccountKind.Microsoft, "受控外观角色", new string('a',32));
        service.Base.Publish(service.Snapshot with { Accounts = [account], SelectedAccountId = account.Id });
        service.Read = (_, _, _) => Task.FromResult(new AccountAppearance(account.Id, AccountSkinModel.Classic, AccountAppearancePng.Decode(Png()),
            [new("cape-A", "甲披风", true, null), new("cape-B", "乙披风", false, null)], false, AccountAppearanceClient.ManagementUri(account), "", true));
        return service;
    }
    [AvaloniaFact]
    public async Task OnlineSkinMenuOpensSharedPreviewAndPickerCancelNeverUploads()
    {
        var service = MicrosoftAppearanceService(); var writes = 0;
        service.Write = (_, _, _) => { writes++; throw new InvalidOperationException(); };
        using var fixture = await Fixture.OpenAsync(service: service); OpenMenu(fixture.Window);
        Menu(fixture.Window, "ChangeAccountSkinMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Find<Button>(fixture.Window, "AccountSkinMenuButton").Flyout!.Hide(); Pump(fixture.Window);
        Assert.True(fixture.Vm.IsOnlineSkinPage); Assert.True(Find<Button>(fixture.Window,"PickOnlineSkinButton").IsEnabled);
        var target = fixture.Vm.CaptureOnlineSkinTarget()!; Assert.False(await fixture.Vm.PreviewPickedOnlineSkinAsync(null,target)); fixture.Vm.EndLocalSkinPicker(target);
        Assert.False(fixture.Vm.UploadOnlineSkinCommand.CanExecute(null)); Assert.Equal(0,writes); Assert.Empty(fixture.External.OpenedUris);
    }
    [AvaloniaFact]
    public async Task PickedOnlineSkinRequiresExplicitUploadAndUnknownOutcomeBlocksRepeatUntilFreshRead()
    {
        var service = MicrosoftAppearanceService(); var writes=0;
        service.Write = (id, change, _) => { Assert.Equal(service.Snapshot.SelectedAccountId,id); Assert.Equal(AccountAppearanceAction.UploadSkin,change.Action); Assert.Equal(Png(),change.Png.ToArray()); writes++; return Task.FromResult(new AccountAppearanceWriteResult(AccountAppearanceWriteState.OutcomeUnknown,null,"结果未能确认，请刷新")); };
        using var fixture = await Fixture.OpenAsync(service: service); var vm = fixture.Vm;
        var target=vm.CaptureOnlineSkinTarget()!; Assert.True(await vm.PreviewPickedOnlineSkinAsync(fixture.PngPath,target)); vm.EndLocalSkinPicker(target);
        Assert.Equal(0,writes); Assert.True(vm.HasOnlineSkinDraft); await vm.UploadOnlineSkinCommand.ExecuteAsync(null);
        Assert.Equal(1,writes); Assert.False(vm.UploadOnlineSkinCommand.CanExecute(null)); Assert.Contains("刷新",vm.AccountAppearanceStatus);
        await vm.RefreshAppearanceCommand.ExecuteAsync(null); Assert.True(vm.UploadOnlineSkinCommand.CanExecute(null)); Assert.Equal(1,writes);
    }
    [AvaloniaFact]
    public async Task CapeSelectionOnlyPreviewsAndExplicitWearOrHideUseCapturedOwnedId()
    {
        var service=MicrosoftAppearanceService(); var actions=new List<AccountAppearanceChange>();
        service.Write=async (_,change,_)=> { actions.Add(change); var before=await service.Read!("",true,default); return new(AccountAppearanceWriteState.Verified,before with { Capes=before.Capes.Select(c=>c with { IsActive=change.Action==AccountAppearanceAction.WearCape && c.Id==change.CapeId }).ToArray() },"已更新"); };
        using var fixture=await Fixture.OpenAsync(service:service); var vm=fixture.Vm; vm.SelectedPreviewCape=vm.AccountCapes[1];
        Assert.Empty(actions); await vm.WearOnlineCapeCommand.ExecuteAsync(null); Assert.Equal("cape-B",actions.Single().CapeId); Assert.True(vm.AccountCapes[1].IsActive);
        await vm.HideOnlineCapeCommand.ExecuteAsync(null); Assert.Equal(2,actions.Count); Assert.Equal(AccountAppearanceAction.HideCape,actions[1].Action); Assert.DoesNotContain(vm.AccountCapes,c=>c.IsActive);
    }
    [AvaloniaFact]
    public async Task PickerResultFromPreviousAccountCannotBecomeNewAccountsUploadDraft()
    {
        var service=MicrosoftAppearanceService(); using var fixture=await Fixture.OpenAsync(service:service); var vm=fixture.Vm;
        var target=vm.CaptureOnlineSkinTarget()!;
        service.Base.Publish(service.Snapshot with { Accounts=[new("offline:replacement",AccountKind.Offline,"另一个角色",new string('b',32))],SelectedAccountId="offline:replacement" }); Pump(fixture.Window);
        Assert.False(await vm.PreviewPickedOnlineSkinAsync(fixture.PngPath,target)); vm.EndLocalSkinPicker(target); Assert.False(vm.HasOnlineSkinDraft); Assert.False(vm.SupportsOnlineAppearance);
    }
}
