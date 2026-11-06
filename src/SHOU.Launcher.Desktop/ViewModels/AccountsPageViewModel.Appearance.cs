using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SkinModel = SHOU.Launcher.Core.Abstractions.AccountSkinModel;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record AccountSkinModelChoice(AccountSkinModel Value, string Name);
public sealed record AccountSkinSelectionTarget(string AccountId, string Identity, long Generation, AccountSkinModel Model);
public enum AccountAppearanceDialogPage { Preview, LocalSkin, Capes }
public sealed class AccountAppearanceRow(AccountProfile profile) : ObservableObject
{
    public AccountProfile Profile { get; private set; } = profile;
    public string DisplayName => Profile.DisplayName;
    public string SourceName => AccountsPageViewModel.SourceName(Profile);
    private AccountTextureImage? skin;
    public AccountTextureImage? Skin { get => skin; internal set => SetProperty(ref skin, value); }
    internal void Update(AccountProfile profile)
    { Profile = profile; OnPropertyChanged(nameof(Profile)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(SourceName)); }
}

public sealed partial class AccountsPageViewModel
{
    private readonly Dictionary<string, AccountAppearance> appearanceCache = new(StringComparer.Ordinal);
    private IAccountAppearanceService? appearanceService;
    private string? appearanceIdentity;
    private long appearanceGeneration;
    private CancellationTokenSource? appearanceReadCancellation;
    private bool appearanceReady, appearanceReadScheduled, appearanceRead, appearanceBusy, appearanceDialogOpen, appearancePickerActive;
    private bool allow3DPreview = true, showSkinOuterLayer = true, showPreviewCape = true;
    private string appearanceMessage = "";
    private AccountAppearance? accountAppearance;
    private AccountCapeAppearance? selectedPreviewCape;
    private AccountAppearanceDialogPage appearanceDialogPage;
    private AccountSkinModelChoice localSkinModel = new(SkinModel.Classic, "经典模型（4 像素手臂）");
    public ObservableCollection<AccountAppearanceRow> AppearanceAccountRows { get; } = [];
    public ObservableCollection<AccountCapeAppearance> AccountCapes { get; } = [];
    public IReadOnlyList<AccountSkinModelChoice> SkinModelChoices { get; } = [new(SkinModel.Classic, "经典模型（4 像素手臂）"), new(SkinModel.Slim, "纤细模型（3 像素手臂）")];
    public AccountAppearanceRow? SelectedAppearanceAccountRow
    {
        get => AppearanceAccountRows.FirstOrDefault(row => row.Profile.Id == SelectedAccount?.Id);
        set { if (value is not null) SelectedAccount = value.Profile; }
    }
    public AccountSkinModelChoice SelectedLocalSkinModel { get => localSkinModel; set { if (value is not null) SetProperty(ref localSkinModel, value); } }
    public AccountTextureImage? AccountSkin => accountAppearance?.Skin;
    public AccountSkinModel AccountSkinModel => accountAppearance?.Model ?? SkinModel.Classic;
    public bool HasAccountSkin => AccountSkin is not null;
    public bool HasAccountCapes => AccountCapes.Count > 0;
    public bool IsAccountAppearanceBusy => appearanceBusy;
    public string AccountAppearanceStatus => appearanceMessage;
    public bool HasAccountAppearanceMessage => appearanceMessage.Length > 0;
    public string AccountAppearanceCapability => accountAppearance?.Capability ?? (appearanceService is null ? "当前服务没有提供外观读取能力。" : SelectedChannelAccount is { Kind: AccountKind.Offline } local ? AccountAppearanceStore.EmptyOffline(local.Id).Capability : "点击“刷新头像”读取真实皮肤与披风；在线修改请前往官网。");
    public string AccountAppearanceSummary => !HasAccountSkin ? "尚无已读取的皮肤" : (AccountSkinModel == SkinModel.Slim ? "纤细模型" : "经典模型") + (AccountCapes.Any(c => c.IsActive) ? " · 已读取当前披风" : " · 未穿戴披风");
    public string AccountAppearancePlayer => SelectedPlayerName + " · " + SelectedPlayerSource;
    public bool CanChooseLocalSkin => CanEdit && !appearanceBusy && !appearancePickerActive && appearanceService is not null && SelectedChannelAccount is { Kind: AccountKind.Offline };
    public bool CanSaveSkinPng => HasAccountSkin && !appearanceBusy;
    public bool CanOpenAppearanceWebsite => SelectedChannelAccount is { } account && AccountAppearanceClient.ManagementUri(account) is not null;
    public bool CanPreviewAccountSkin => allow3DPreview && HasAccountSkin;
    public bool Allow3DPreview { get => allow3DPreview; set { if (SetProperty(ref allow3DPreview, value)) { if (!value && IsAccountPreviewPage) CloseAccountAppearance(); OnPropertyChanged(nameof(CanPreviewAccountSkin)); } } }
    public bool ShowSkinOuterLayer { get => showSkinOuterLayer; set => SetProperty(ref showSkinOuterLayer, value); }
    public bool ShowPreviewCape { get => showPreviewCape; set => SetProperty(ref showPreviewCape, value); }
    public AccountCapeAppearance? SelectedPreviewCape
    { get => selectedPreviewCape; set { if (SetProperty(ref selectedPreviewCape, value)) OnPropertyChanged(nameof(PreviewCapeTexture)); } }
    public AccountTextureImage? PreviewCapeTexture => SelectedPreviewCape?.Texture;
    public bool IsAccountAppearanceDialogOpen => appearanceDialogOpen;
    public bool IsAccountPreviewPage => appearanceDialogPage == AccountAppearanceDialogPage.Preview;
    public bool IsLocalSkinPage => appearanceDialogPage == AccountAppearanceDialogPage.LocalSkin;
    public bool IsAccountCapePage => appearanceDialogPage == AccountAppearanceDialogPage.Capes;
    public string AccountAppearanceDialogTitle => appearanceDialogPage switch { AccountAppearanceDialogPage.LocalSkin => "本地离线皮肤", AccountAppearanceDialogPage.Capes => "角色披风", _ => "3D 皮肤与披风" };
    public string AccountCapeSummary => SelectedChannelAccount?.Kind == AccountKind.Yggdrasil ? "公开纹理仅提供当前披风；其他披风请在对应官网查看。" : SelectedChannelAccount?.Kind == AccountKind.Offline ? "离线角色没有在线披风列表。" : $"已读取 {AccountCapes.Count} 件披风；选中仅改变预览，穿戴请前往官网。";
    public AsyncRelayCommand RefreshAppearanceCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenAppearanceWebsiteCommand { get; private set; } = null!;
    public RelayCommand CloseAppearanceCommand { get; private set; } = null!;
    public RelayCommand CancelAppearanceReadCommand { get; private set; } = null!;

    private void InitializeAccountAppearance()
    {
        appearanceService = Service as IAccountAppearanceService;
        RefreshAppearanceCommand = AsyncCommand(() => ReadAccountAppearanceAsync(true), () => appearanceService is not null && SelectedChannelAccount is not null && Owner.CanReadRemote && !appearanceBusy);
        OpenAppearanceWebsiteCommand = AsyncCommand(async () =>
        {
            var account = SelectedChannelAccount;
            if (account is null || AccountAppearanceClient.ManagementUri(account) is not { } uri) return;
            await Owner.RunControlAsync("打开此外观官网", ct => Owner.ExternalLauncher.OpenUriAsync(uri, ct));
        }, () => CanOpenAppearanceWebsite);
        CloseAppearanceCommand = Command(CloseAccountAppearance);
        CancelAppearanceReadCommand = Command(() => appearanceReadCancellation?.Cancel(), () => appearanceBusy);
        appearanceReady = true;
    }
    private static string AppearanceIdentity(AccountProfile account) => account.Id + "\n" + account.Kind + "\n" + account.PlayerUuid + "\n" + account.AuthenticationServer;
    private void RefreshAccountAppearanceRows()
    {
        if (!appearanceReady) return;
        var previous = AppearanceAccountRows.ToDictionary(row => row.Profile.Id, StringComparer.Ordinal);
        var next = Accounts.Select(account => { var row = previous.GetValueOrDefault(account.Id) ?? new AccountAppearanceRow(account); row.Update(account); row.Skin = appearanceCache.GetValueOrDefault(AppearanceIdentity(account))?.Skin; return row; }).ToArray();
        if (!AppearanceAccountRows.SequenceEqual(next)) { AppearanceAccountRows.Clear(); foreach (var row in next) AppearanceAccountRows.Add(row); }
        foreach (var key in appearanceCache.Keys.Where(key => !Accounts.Any(a => AppearanceIdentity(a) == key)).ToArray()) appearanceCache.Remove(key);
        OnPropertyChanged(nameof(SelectedAppearanceAccountRow)); RefreshAccountAppearanceIdentity();
    }
    private void RefreshAccountAppearanceIdentity()
    {
        if (!appearanceReady) return;
        OnPropertyChanged(nameof(SelectedAppearanceAccountRow));
        var identity = SelectedChannelAccount is { } account ? AppearanceIdentity(account) : null;
        if (appearanceIdentity == identity) { OnPropertyChanged(nameof(AccountAppearancePlayer)); NotifyAccountAppearanceCommands(); return; }
        ++appearanceGeneration; appearanceReadCancellation?.Cancel(); appearanceReadCancellation = null;
        appearanceIdentity = identity; appearanceRead = false; appearanceBusy = false; appearancePickerActive = false;
        CloseAccountAppearance(); appearanceMessage = ""; accountAppearance = identity is null ? null : appearanceCache.GetValueOrDefault(identity);
        UpdateAccountAppearance(accountAppearance); ScheduleUnreadAccountAppearance();
    }
    private void ScheduleUnreadAccountAppearance()
    {
        if (!appearanceReady || appearanceReadScheduled || appearanceRead || appearanceBusy || appearanceService is null || appearanceIdentity is null || !IsAppearancePageActive) return;
        appearanceReadScheduled = true;
        Dispatcher.UIThread.Post(() => { appearanceReadScheduled = false; if (appearanceRead || appearanceBusy || appearanceIdentity is null || !Owner.CanReadRemote || !IsAppearancePageActive) return; _ = ReadAccountAppearanceAsync(false); });
    }
    public async Task ReadAccountAppearanceAsync(bool refreshRemote)
    {
        if (appearanceService is null || SelectedChannelAccount is not { } account || appearanceBusy) return;
        var identity = AppearanceIdentity(account); var generation = appearanceGeneration;
        using var cancellation = new CancellationTokenSource(); appearanceReadCancellation = cancellation; appearanceBusy = true; appearanceMessage = "正在读取外观…"; NotifyAppearancePresentation();
        try
        {
            var result = await appearanceService.GetAccountAppearanceAsync(account.Id, refreshRemote, cancellation.Token);
            // A first visit should show the selected online character, without requiring a manual refresh.
            // Read cache first; preserve cancellation and identity checks before issuing the remote request.
            if (!refreshRemote && result.Skin is null && account.Kind != AccountKind.Offline
                && !cancellation.IsCancellationRequested && IsCurrentAppearance(identity, generation))
                result = await appearanceService.GetAccountAppearanceAsync(account.Id, true, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsCurrentAppearance(identity, generation) || result.AccountId != account.Id) return;
            CacheAppearance(identity, result); appearanceRead = true; appearanceMessage = result.Skin is null ? account.Kind == AccountKind.Offline ? "尚未选择本地皮肤。" : result.IsRemote ? "服务未返回当前皮肤。" : "点击“刷新头像”读取在线外观。" : refreshRemote ? "外观已刷新" : "";
            UpdateAccountAppearance(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (IsCurrentAppearance(identity, generation)) { appearanceRead = true; appearanceMessage = "读取已取消，已有外观保留。"; } }
        catch (Exception ex) { if (IsCurrentAppearance(identity, generation)) { appearanceRead = true; appearanceMessage = UiPrivacy.Redact(ex.Message); } }
        finally
        {
            if (ReferenceEquals(appearanceReadCancellation, cancellation)) appearanceReadCancellation = null;
            if (IsCurrentAppearance(identity, generation)) { appearanceBusy = false; NotifyAppearancePresentation(); }
        }
    }
    private bool IsCurrentAppearance(string identity, long generation) => appearanceIdentity == identity && appearanceGeneration == generation && SelectedChannelAccount is { } selected && AppearanceIdentity(selected) == identity;
    private void UpdateAccountAppearance(AccountAppearance? value)
    {
        accountAppearance = value; AccountCapes.Clear(); foreach (var cape in value?.Capes ?? []) AccountCapes.Add(cape);
        SelectedPreviewCape = AccountCapes.FirstOrDefault(c => c.IsActive) ?? AccountCapes.FirstOrDefault();
        if (value is not null && AppearanceAccountRows.FirstOrDefault(row => row.Profile.Id == value.AccountId) is { } row) row.Skin = value.Skin;
        SelectedLocalSkinModel = SkinModelChoices.First(c => c.Value == (value?.Model ?? SkinModel.Classic)); NotifyAppearancePresentation();
    }
    public AccountSkinSelectionTarget? CaptureLocalSkinTarget()
    {
        if (!CanChooseLocalSkin || SelectedChannelAccount is not { } account || appearanceIdentity is null) return null;
        appearancePickerActive = true; NotifyAppearancePresentation();
        return new(account.Id, appearanceIdentity, appearanceGeneration, SelectedLocalSkinModel.Value);
    }
    public void EndLocalSkinPicker(AccountSkinSelectionTarget target)
    { if (IsCurrentAppearance(target.Identity, target.Generation)) { appearancePickerActive = false; NotifyAppearancePresentation(); } }
    public async Task<bool> SavePickedSkinAsync(string? path, AccountSkinSelectionTarget target)
    {
        if (path is null) return false;
        if (!IsCurrentAppearance(target.Identity, target.Generation) || SelectedChannelAccount?.Id != target.AccountId || appearanceService is null)
        { Owner.ShowError("当前角色已改变，所选皮肤未保存。请为当前角色重新选择。"); return false; }
        AccountAppearance? result = null;
        var saved = await Owner.RunAsync("保存本地皮肤", async ct =>
        {
            var png = await AccountAppearanceStore.ReadPickedSkinAsync(path, ct);
            if (!IsCurrentAppearance(target.Identity, target.Generation)) throw new InvalidOperationException("读取期间角色已改变，皮肤未保存。");
            result = await appearanceService.SaveOfflineAccountSkinAsync(target.AccountId, png, target.Model, ct);
        });
        if (saved && result is not null && IsCurrentAppearance(target.Identity, target.Generation))
        {
            CacheAppearance(target.Identity, result); appearanceRead = true; appearanceMessage = "已保存此离线角色的本地皮肤。"; UpdateAccountAppearance(result); return true;
        }
        return false;
    }
    public bool OpenAccountAppearance(AccountAppearanceDialogPage page)
    {
        if (SelectedChannelAccount is null || page == AccountAppearanceDialogPage.Preview && !CanPreviewAccountSkin || page == AccountAppearanceDialogPage.LocalSkin && SelectedChannelAccount.Kind != AccountKind.Offline) return false;
        appearanceDialogPage = page; appearanceDialogOpen = true; NotifyAppearancePresentation(); return true;
    }
    public void CloseAccountAppearance()
    { if (appearanceDialogOpen) { appearanceDialogOpen = false; OnPropertyChanged(nameof(IsAccountAppearanceDialogOpen)); } }
    private void CancelAccountAppearanceWork()
    { appearanceReadCancellation?.Cancel(); CloseAccountAppearance(); }
    private void CacheAppearance(string identity, AccountAppearance value)
    {
        appearanceCache.Remove(identity);
        while (appearanceCache.Count >= 8) appearanceCache.Remove(appearanceCache.Keys.First());
        appearanceCache[identity] = value;
    }
    private bool IsAppearancePageActive => ReferenceEquals(Owner.SelectedPage, Owner.Home) || ReferenceEquals(Owner.SelectedPage, this);
    private void NotifyAppearancePresentation()
    {
        foreach (var property in new[] { nameof(AccountSkin), nameof(AccountSkinModel), nameof(HasAccountSkin), nameof(HasAccountCapes), nameof(IsAccountAppearanceBusy), nameof(AccountAppearanceStatus), nameof(HasAccountAppearanceMessage), nameof(AccountAppearanceCapability), nameof(AccountAppearanceSummary), nameof(AccountAppearancePlayer), nameof(CanChooseLocalSkin), nameof(CanSaveSkinPng), nameof(CanOpenAppearanceWebsite), nameof(CanPreviewAccountSkin), nameof(IsAccountAppearanceDialogOpen), nameof(IsAccountPreviewPage), nameof(IsLocalSkinPage), nameof(IsAccountCapePage), nameof(AccountAppearanceDialogTitle), nameof(AccountCapeSummary) }) OnPropertyChanged(property);
        NotifyAccountAppearanceCommands();
    }
    private void NotifyAccountAppearanceCommands()
    {
        if (!appearanceReady) return;
        OnPropertyChanged(nameof(CanChooseLocalSkin)); OnPropertyChanged(nameof(CanSaveSkinPng));
        RefreshAppearanceCommand.NotifyCanExecuteChanged(); OpenAppearanceWebsiteCommand.NotifyCanExecuteChanged(); CancelAppearanceReadCommand.NotifyCanExecuteChanged(); ScheduleUnreadAccountAppearance();
    }
}
