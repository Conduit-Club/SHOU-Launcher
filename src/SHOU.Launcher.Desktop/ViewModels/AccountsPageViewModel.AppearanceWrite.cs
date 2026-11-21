using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class AccountsPageViewModel
{
    private AccountTextureImage? onlineSkinDraft;
    private bool onlineWriteNeedsRefresh;
    public bool SupportsOnlineAppearance => Service is IAccountAppearanceMutationService && SelectedChannelAccount?.Kind == AccountKind.Microsoft;
    public bool CanChooseOnlineSkin => SupportsOnlineAppearance && CanEdit && !appearanceBusy && !appearancePickerActive;
    public AccountTextureImage? OnlineSkinPreview => onlineSkinDraft ?? AccountSkin;
    public bool HasOnlineSkinDraft => onlineSkinDraft is not null;
    public AsyncRelayCommand UploadOnlineSkinCommand { get; private set; } = null!;
    public AsyncRelayCommand WearOnlineCapeCommand { get; private set; } = null!;
    public AsyncRelayCommand HideOnlineCapeCommand { get; private set; } = null!;
    private bool CanWriteOnlineAppearance => SupportsOnlineAppearance && CanEdit && !appearanceBusy && !appearancePickerActive && !onlineWriteNeedsRefresh;
    private void InitializeOnlineAppearance()
    {
        UploadOnlineSkinCommand = AsyncCommand(() => WriteOnlineAppearanceAsync(new(AccountAppearanceAction.UploadSkin, onlineSkinDraft!.Png, SelectedLocalSkinModel.Value)), () => CanWriteOnlineAppearance && HasOnlineSkinDraft);
        WearOnlineCapeCommand = AsyncCommand(() => WriteOnlineAppearanceAsync(new(AccountAppearanceAction.WearCape, CapeId: SelectedPreviewCape!.Id)), () => CanWriteOnlineAppearance && SelectedPreviewCape is { IsActive: false });
        HideOnlineCapeCommand = AsyncCommand(() => WriteOnlineAppearanceAsync(new(AccountAppearanceAction.HideCape)), () => CanWriteOnlineAppearance && AccountCapes.Any(c => c.IsActive));
    }
    public AccountSkinSelectionTarget? CaptureOnlineSkinTarget()
    {
        if (!CanChooseOnlineSkin || SelectedChannelAccount is not { } account || appearanceIdentity is null) return null;
        appearancePickerActive = true; NotifyAppearancePresentation();
        return new(account.Id, appearanceIdentity, appearanceGeneration, SelectedLocalSkinModel.Value);
    }
    public async Task<bool> PreviewPickedOnlineSkinAsync(string? path, AccountSkinSelectionTarget target)
    {
        if (path is null) return false;
        try
        {
            var png = await AccountAppearanceStore.ReadPickedSkinAsync(path);
            if (!IsCurrentAppearance(target.Identity, target.Generation)) { Owner.ShowError("当前角色已改变，请重新选择皮肤。"); return false; }
            onlineSkinDraft = AccountAppearancePng.Decode(png); appearanceMessage = "预览已就绪，点击“上传到此角色”后生效。"; NotifyAppearancePresentation(); return true;
        }
        catch (Exception ex) { if (IsCurrentAppearance(target.Identity, target.Generation)) { appearanceMessage = UiPrivacy.Redact(ex.Message); NotifyAppearancePresentation(); } return false; }
    }
    private async Task WriteOnlineAppearanceAsync(AccountAppearanceChange change)
    {
        if (!CanWriteOnlineAppearance || Service is not IAccountAppearanceMutationService writer || SelectedChannelAccount is not { } account || appearanceIdentity is not { } identity) return;
        var generation = appearanceGeneration;
        using var cancellation = new CancellationTokenSource(); appearanceReadCancellation = cancellation; appearanceBusy = true;
        appearanceMessage = "正在更新在线外观…"; NotifyAppearancePresentation();
        try
        {
            await Owner.RunInlineAsync("更新在线外观", async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellation.Token);
                var result = await writer.ChangeAccountAppearanceAsync(account.Id, change, linked.Token);
                if (!IsCurrentAppearance(identity, generation)) { Owner.ShowError("此前角色的外观操作已结束，请刷新该角色核对结果。"); return; }
                appearanceMessage = result.Message;
                onlineWriteNeedsRefresh = result.State is AccountAppearanceWriteState.AcceptedNeedsRefresh or AccountAppearanceWriteState.OutcomeUnknown;
                if (result.Appearance is { } updated) { CacheAppearance(identity, updated); appearanceRead = true; UpdateAccountAppearance(updated); }
                else if (onlineWriteNeedsRefresh) appearanceCache.Remove(identity);
                if (result.State == AccountAppearanceWriteState.Verified && change.Action == AccountAppearanceAction.UploadSkin) onlineSkinDraft = null;
            });
        }
        finally
        {
            if (ReferenceEquals(appearanceReadCancellation, cancellation)) appearanceReadCancellation = null;
            if (IsCurrentAppearance(identity, generation)) { appearanceBusy = false; NotifyAppearancePresentation(); }
        }
    }
    private void NotifyOnlineAppearance()
    {
        foreach (var property in new[] { nameof(SupportsOnlineAppearance), nameof(CanChooseOnlineSkin), nameof(OnlineSkinPreview), nameof(HasOnlineSkinDraft), nameof(IsOnlineSkinPage) }) OnPropertyChanged(property);
        UploadOnlineSkinCommand?.NotifyCanExecuteChanged(); WearOnlineCapeCommand?.NotifyCanExecuteChanged(); HideOnlineCapeCommand?.NotifyCanExecuteChanged();
    }
}
